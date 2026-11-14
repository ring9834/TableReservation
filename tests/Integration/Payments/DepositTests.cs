using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Payments;

/// <summary>Taking a deposit (AC1.5, BR-06, Design §8.4): the intent after commit, replays, Stripe down, the status poll.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DepositTests(PostgresFixture postgres) : PaymentTestBase(postgres)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task An_online_party_of_6_or_more_pays_20_dollars_a_guest_and_smaller_parties_nothing_BR_06(int partySize)
    {
        var before = Infra.Now;
        var created = await BookAsync(partySize);

        if (partySize < 6)
        {
            Assert.Equal(BookingStatus.Booked, created.Status);
            Assert.Null(created.Deposit);
            Assert.Null(created.ClientSecret);
            Assert.Empty(Payments.Calls);
            return;
        }

        Assert.Equal(BookingStatus.PendingPayment, created.Status);
        Assert.Equal(partySize * 2_000, created.Deposit!.AmountCents);
        Assert.Equal("NZD", created.Deposit.Currency);
        // Real time (infrastructure clock), not 18:25; stored to the microsecond.
        Assert.Equal(before.AddMinutes(10).UtcDateTime, created.HoldExpiresAt!.Value, TimeSpan.FromMilliseconds(1));

        var call = Assert.Single(Payments.Deposits);
        Assert.Equal($"deposit:{created.BookingId}", call.IdempotencyKey);
        Assert.Equal(Money.FromNzd(partySize * 2_000), call.Amount);
        Assert.Equal(created.BookingId, call.Request!.BookingId.Value);
        Assert.Equal(GoldenDragon.VenueId, call.Request.VenueId);
        Assert.Equal(created.Reference, call.Request.Reference);

        var booking = await LoadAsync(created.BookingId);
        Assert.NotNull(booking.Deposit!.PaymentIntentId);
        Assert.Equal($"{booking.Deposit.PaymentIntentId}_secret_fake", created.ClientSecret);
        Assert.Equal(booking.Version, created.Version);
    }

    [Fact]
    public async Task A_phone_booking_of_6_takes_no_deposit_BR_06()
    {
        await SignInAsync(Client);
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/phone-bookings", BookingBody(Tomorrow, 19, 0, 6)), HttpStatusCode.Created);

        Assert.Equal(BookingStatus.Booked, created.Status);
        Assert.Null(created.Deposit);
        Assert.Null(created.ClientSecret);
        Assert.Empty(Payments.Calls);
    }

    [Fact]
    public async Task The_provider_is_called_after_the_commit_with_no_lock_held()
    {
        var checkedLocks = false;
        Payments.OnCreateDeposit = async request =>
        {
            // Another connection sees the committed booking, and no advisory lock (slot or table) is held by anyone.
            await using var connection = await Database.DataSource.OpenConnectionAsync(Ct);
            await using (var status = new NpgsqlCommand("SELECT status FROM bookings WHERE id = @id", connection))
            {
                status.Parameters.AddWithValue("id", request.BookingId.Value);
                Assert.Equal(nameof(BookingStatus.PendingPayment), await status.ExecuteScalarAsync(Ct));
            }

            await using var locks = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'", connection);
            Assert.Equal(0L, await locks.ExecuteScalarAsync(Ct));
            checkedLocks = true;
        };

        await BookAsync();

        Assert.True(checkedLocks);
    }

    [Fact]
    public async Task A_replay_with_the_same_key_returns_the_same_intent_and_client_secret_and_the_secret_is_never_stored()
    {
        var first = await BookAsync(idempotencyKey: "replay-deposit-1");
        var second = await BookAsync(idempotencyKey: "replay-deposit-1");

        Assert.Equal(first.BookingId, second.BookingId);
        Assert.NotNull(first.ClientSecret);
        Assert.Equal(first.ClientSecret, second.ClientSecret);
        Assert.Equal(first.Version, second.Version);

        // Both calls carry the booking's key, so Stripe returns one intent.
        Assert.All(Payments.Deposits, c => Assert.Equal($"deposit:{first.BookingId}", c.IdempotencyKey));
        await using var db = Database.Context();
        Assert.Equal(1, await db.Bookings.CountAsync(b => b.Deposit!.PaymentIntentId!.StartsWith("pi_fake_"), Ct)); // seeded ones are pi_seed_*
        var stored = await db.IdempotencyKeys.AsNoTracking().SingleAsync(k => k.Key == "replay-deposit-1", Ct);
        using var storedJson = JsonDocument.Parse(stored.Response);
        Assert.Equal(JsonValueKind.Null, storedJson.RootElement.GetProperty("clientSecret").ValueKind);
        Assert.DoesNotContain(first.ClientSecret!, stored.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stripe_down_is_502_and_the_booking_waits_until_its_hold_expires()
    {
        Payments.Fail = true;

        var response = await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 0, 6), "stripe-down-1");

        await ProblemAssert.IsAsync(response, HttpStatusCode.BadGateway, "payment_provider_error");
        await using var db = Database.Context();
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.PartySize == PartySize.Of(6) && b.Status == BookingStatus.PendingPayment, Ct);
        Assert.Null(booking.Deposit!.PaymentIntentId);

        Infra.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await HoldExpiry.RunOnceAsync(Ct));

        var expired = await LoadAsync(booking.Id.Value);
        Assert.Equal(BookingStatus.Cancelled, expired.Status);
        Assert.Equal(CancelReason.HoldExpired, expired.CancellationReason);
        Assert.Equal(DepositStatus.Released, expired.Deposit!.Status);
    }

    [Fact]
    public async Task After_a_provider_failure_the_guest_retries_with_the_same_key_and_gets_the_client_secret()
    {
        Payments.Fail = true;
        var body = BookingBody(Tomorrow, 19, 0, 6, mobile: "021 011 0301");
        Assert.Equal(HttpStatusCode.BadGateway, (await PostAsync(Client, $"{Venue}/bookings", body, "retry-1")).StatusCode);

        Payments.Fail = false;
        var retried = await BookAsync(idempotencyKey: "retry-1");

        Assert.Equal(BookingStatus.PendingPayment, retried.Status);
        Assert.NotNull(retried.ClientSecret);
        await using var db = Database.Context();
        var withDeposit = await db.Bookings.AsNoTracking().Where(b => b.Status == BookingStatus.PendingPayment).ToListAsync(Ct);
        Assert.Equal(retried.BookingId, Assert.Single(withDeposit).Id.Value); // one booking: the replay, not a second one
        Assert.NotNull(withDeposit[0].Deposit!.PaymentIntentId);
    }

    [Fact]
    public async Task The_status_poll_shows_states_only_and_needs_no_sign_in()
    {
        var created = await BookAsync();

        var response = await NewClient().GetAsync($"{Venue}/bookings/{created.BookingId}/status", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(
            ["bookingId", "status", "depositStatus", "cancelReason", "holdExpiresAt"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("pendingPayment", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("pending", json.RootElement.GetProperty("depositStatus").GetString());
        Assert.Equal(created.HoldExpiresAt, json.RootElement.GetProperty("holdExpiresAt").GetDateTime());
        Assert.DoesNotContain("Test Guest", body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync($"{Venue}/bookings/{Guid.NewGuid()}/status", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NewClient().GetAsync($"{OtherVenue}/bookings/{created.BookingId}/status", Ct)).StatusCode);
    }
}
