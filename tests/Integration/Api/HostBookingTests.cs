using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>Host endpoints: phone bookings, tonight's list, confirm and cancel (Design §14, §14.1).</summary>
[Collection(PostgresCollection.Name)]
public sealed class HostBookingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Seated = "GD2A4K"; // #1, seated on T4
    private const string Booked = "GDA2J6"; // #9, 19:15, booked on T6
    private const string PaidDeposit = "GDC4L8"; // #11, 20:30, deposit captured

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync(Client);
    }

    [Fact]
    public async Task A_phone_booking_never_takes_a_deposit_and_records_the_host_as_actor()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + "/phone-bookings", BookingBody(Tomorrow, 19, 0, 8)), HttpStatusCode.Created);

        Assert.Equal(BookingStatus.Booked, created.Status);
        Assert.Null(created.Deposit);
        Assert.Equal([GoldenDragon.TableId("T10").Value], created.TableIds);
        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(created.BookingId), Ct);
        Assert.Equal(BookingChannel.Phone, booking.Channel);
        var actor = (await db.Outbox.SingleAsync(o => o.AggregateId == created.BookingId, Ct)).Envelope.RootElement.GetProperty("actor");
        Assert.Equal("staff", actor.GetProperty("type").GetString());
        Assert.Equal("demo-host", actor.GetProperty("id").GetString());
        Assert.Equal("Host", actor.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_phone_booking_may_be_larger_than_the_online_limit_but_still_needs_a_table()
    {
        var response = await PostAsync(Client, Venue + "/phone-bookings", BookingBody(Tomorrow, 19, 0, 10));

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "slot_taken"); // no table seats 10
    }

    [Fact]
    public async Task A_phone_booking_needs_a_session()
    {
        using var guest = NewClient();

        var response = await PostAsync(guest, Venue + "/phone-bookings", BookingBody(Tomorrow, 19, 0, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.Unauthorized, "not_signed_in");
    }

    [Fact]
    public async Task Tonight_lists_every_seeded_booking_in_time_order()
    {
        var list = await ReadAsync<BookingListResponse>(await Client.GetAsync(Venue + "/bookings", Ct));

        Assert.Equal(DemoTime.Tonight, list.Day);
        Assert.Equal(11, list.Items.Count);
        Assert.Equal(list.Items.OrderBy(i => i.StartsAt).Select(i => i.Id), list.Items.Select(i => i.Id));
        var seated = list.Items.Single(i => i.Reference == Seated);
        Assert.Equal(BookingStatus.Seated, seated.Status);
        Assert.Equal("T4", Assert.Single(seated.Tables).Name);
        Assert.Equal("王伟 Wang Wei", seated.GuestName);
        Assert.Equal("+64******001", seated.MobileMasked);
        Assert.True(seated.Version > 0);
        var paid = list.Items.Single(i => i.Reference == PaidDeposit);
        Assert.Equal(12_000, paid.Deposit!.AmountCents);
        Assert.Equal(DepositStatus.Captured, paid.DepositStatus);
    }

    [Fact]
    public async Task Another_day_lists_only_that_day()
    {
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Venue + "/phone-bookings", BookingBody(Tomorrow, 18, 0, 2)), HttpStatusCode.Created);

        var list = await ReadAsync<BookingListResponse>(await Client.GetAsync($"{Venue}/bookings?day={Tomorrow:yyyy-MM-dd}", Ct));

        Assert.Equal(Tomorrow, list.Day);
        Assert.Single(list.Items);
    }

    [Fact]
    public async Task Confirm_returns_the_new_state_and_version_and_a_repeat_is_a_no_op()
    {
        var before = await ItemAsync(Booked);

        var confirmed = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Action(before, "confirm"), new { version = before.Version }));
        var again = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Action(before, "confirm"), new { version = before.Version }));

        Assert.Equal(BookingStatus.Confirmed, confirmed.Status);
        Assert.NotEqual(before.Version, confirmed.Version);
        Assert.Equal(confirmed, again);
        await using var db = Database.Context();
        Assert.Equal(1, await db.Outbox.CountAsync(o => o.AggregateId == before.Id && o.EventType == "BookingConfirmed", Ct));
    }

    [Fact]
    public async Task Cancel_frees_the_table_with_the_refund_decision()
    {
        var before = await ItemAsync(PaidDeposit);

        var cancelled = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Action(before, "cancel"), new { version = before.Version }));

        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision); // host cancel, terms say full
        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(before.Id), Ct);
        Assert.All(booking.Tables, t => Assert.False(t.Active));
        Assert.Equal(CancelReason.HostRequest, booking.CancellationReason);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("cancel")]
    public async Task A_seated_booking_cannot_be_confirmed_or_cancelled(string action)
    {
        var seated = await ItemAsync(Seated);

        var response = await PostAsync(Client, Action(seated, action), new { version = seated.Version });

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task A_stale_version_is_version_conflict()
    {
        var item = await ItemAsync(Booked);

        var response = await PostAsync(Client, Action(item, "cancel"), new { version = item.Version - 1 });

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "version_conflict");
    }

    [Fact]
    public async Task A_version_read_before_another_host_changed_the_booking_is_version_conflict()
    {
        var item = await ItemAsync(Booked);
        await using (var db = Database.Context())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(item.Id), Ct);
            Assert.True(booking.Confirm(Reservation.Domain.Common.Actor.ForStaff("other-host", "Host"), DemoTime.Now).IsSuccess);
            await db.SaveChangesAsync(Ct);
        }

        var response = await PostAsync(Client, Action(item, "cancel"), new { version = item.Version });

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "version_conflict");
    }

    [Fact]
    public async Task A_missing_version_is_validation_failed()
    {
        var item = await ItemAsync(Booked);

        var response = await Client.PostAsJsonAsync(Action(item, "confirm"), new { }, Ct);

        await ProblemAssert.IsValidationAsync(response, "version", "version_required");
    }

    [Fact]
    public async Task An_unknown_booking_is_not_found()
    {
        var response = await PostAsync(Client, $"{Venue}/bookings/{Guid.CreateVersion7()}/confirm", new { version = 1 });

        await ProblemAssert.IsAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    private static string Action(BookingListItem item, string action) => $"{Venue}/bookings/{item.Id}/{action}";

    private async Task<BookingListItem> ItemAsync(string reference)
    {
        var list = await ReadAsync<BookingListResponse>(await Client.GetAsync(Venue + "/bookings", Ct));
        return list.Items.Single(i => i.Reference == reference);
    }
}
