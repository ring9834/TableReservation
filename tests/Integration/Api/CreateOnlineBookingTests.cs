using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>POST bookings (online, public): AC1.4–AC1.6 without payment, BR-01, BR-03, BR-04, BR-05, BR-06.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CreateOnlineBookingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Path = "/bookings";

    [Fact]
    public async Task Party_of_2_at_19_30_is_booked_on_the_smallest_free_table_AC1_4_AC1_6_BR_05()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight, 19, 30, 2, mobile: "021 011 0199", language: "zh")),
            HttpStatusCode.Created);

        Assert.Equal(BookingStatus.Booked, created.Status);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", created.Reference);
        Assert.Equal([GoldenDragon.TableId("T1").Value], created.TableIds);
        Assert.Equal(DemoTime.At(19, 30).UtcDateTime, created.StartsAt);
        Assert.Equal(DemoTime.At(20, 45).UtcDateTime, created.EndsAt); // 75 minutes (BR-02)
        Assert.True(created.Version > 0);
        Assert.Null(created.Deposit);
        Assert.Null(created.ClientSecret);

        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(created.BookingId), Ct);
        var guest = await db.Guests.SingleAsync(g => g.Id == booking.GuestId, Ct);
        Assert.Equal(BookingChannel.Online, booking.Channel);
        Assert.Equal("+64210110199", guest.Mobile.E164); // AC1.4: international format
        Assert.Equal(Language.Zh, guest.Language);
    }

    [Fact]
    public async Task Party_of_6_is_pending_payment_with_the_deposit_due_AC1_5_BR_06()
    {
        var before = DateTimeOffset.UtcNow;
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight, 20, 0, 6)), HttpStatusCode.Created);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(BookingStatus.PendingPayment, created.Status);
        Assert.Equal(12_000, created.Deposit!.AmountCents); // 6 × NZD 20
        Assert.Equal("NZD", created.Deposit.Currency);
        // M8a: the hold is 10 minutes of real time (infrastructure clock), not of the fixed demo clock.
        Assert.InRange(created.HoldExpiresAt!.Value, before.AddMinutes(10).UtcDateTime, after.AddMinutes(10).UtcDateTime);
        Assert.Equal([GoldenDragon.TableId("T10").Value], created.TableIds);
        Assert.StartsWith("pi_fake_", created.ClientSecret, StringComparison.Ordinal); // the Fake provider (M8a)
    }

    [Fact]
    public async Task Optional_details_are_kept_on_the_booking_and_guest()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(
                DemoTime.Tonight, 19, 30, 2, email: "guest@example.org", notes: "Window please", allergyNotes: "Peanuts", occasion: "Birthday")),
            HttpStatusCode.Created);

        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(created.BookingId), Ct);
        var guest = await db.Guests.SingleAsync(g => g.Id == booking.GuestId, Ct);
        Assert.Equal("Window please", booking.Notes);
        Assert.Equal("Birthday", booking.Occasion);
        Assert.Equal("guest@example.org", guest.Email);
        Assert.Equal("Peanuts", guest.AllergyNotes);
    }

    [Fact]
    public async Task A_returning_mobile_reuses_the_guest()
    {
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 18, 0, 2, mobile: "+64 21 011 0177")), HttpStatusCode.Created);
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 2, mobile: "021 011 0177")), HttpStatusCode.Created);

        await using var db = Database.Context();
        var mobile = PhoneNumber.Create("021 011 0177").Value;
        Assert.Equal(1, await db.Guests.CountAsync(g => g.Mobile == mobile, Ct));
    }

    [Fact]
    public async Task A_returning_guest_takes_the_language_of_the_new_booking_and_keeps_the_stored_name()
    {
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 18, 0, 2, name: "First Name", mobile: "021 011 0178", language: "en")),
            HttpStatusCode.Created);
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 2, name: "Other Name", mobile: "021 011 0178", language: "zh")),
            HttpStatusCode.Created);

        await using var db = Database.Context();
        var mobile = PhoneNumber.Create("021 011 0178").Value;
        var guest = await db.Guests.SingleAsync(g => g.Mobile == mobile, Ct);
        Assert.Equal(Language.Zh, guest.Language);
        Assert.Equal("First Name", guest.Name.Value);
    }

    [Fact]
    public async Task Party_above_the_online_limit_is_party_too_large_BR_04()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 9));

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "party_too_large");
    }

    [Theory]
    [InlineData(19, 7)] // off the 15-minute grid
    [InlineData(21, 0)] // after the last booking at 20:30
    [InlineData(16, 45)] // before service starts
    public async Task A_time_outside_the_slots_is_outside_service_hours_BR_01(int hour, int minute)
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, hour, minute, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "outside_service_hours");
    }

    [Fact]
    public async Task A_slot_that_has_already_started_is_outside_service_hours()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight, 18, 15, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "outside_service_hours");
    }

    [Fact]
    public async Task A_date_beyond_the_booking_window_is_refused()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight.AddDays(31), 19, 0, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "outside_booking_window");
    }

    [Fact]
    public async Task A_slot_at_its_pacing_limit_is_slot_taken_BR_03()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight, 19, 0, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "slot_taken");
    }

    [Fact]
    public async Task No_free_table_that_fits_is_slot_taken()
    {
        // The only 8-seat table is seated until 20:00.
        var response = await PostAsync(Client, Venue + Path, BookingBody(DemoTime.Tonight, 19, 30, 8));

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "slot_taken");
    }

    [Fact]
    public async Task Invalid_details_are_validation_failed_with_field_codes()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 2, name: " ", mobile: "09 300 1234", email: "nope"));

        var problem = await ProblemAssert.IsAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.Equal("name_length", errors.GetProperty("name")[0].GetString());
        Assert.Equal("mobile_not_mobile", errors.GetProperty("mobile")[0].GetString()); // a landline (BR-12)
        Assert.Equal("email_invalid", errors.GetProperty("email")[0].GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_validation_failed()
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await Client.PostAsync(Venue + Path, content, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task An_unknown_venue_is_not_found()
    {
        var response = await PostAsync(Client, OtherVenue + Path, BookingBody(Tomorrow, 19, 0, 2));

        await ProblemAssert.IsAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task The_outbox_row_carries_the_request_correlation_id_and_the_guest_as_actor()
    {
        var correlationId = Guid.CreateVersion7();

        var response = await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 2), correlationId: correlationId);
        var created = await ReadAsync<CreateBookingResponse>(response, HttpStatusCode.Created);

        Assert.Equal(correlationId.ToString(), response.Headers.GetValues("X-Correlation-Id").Single());
        await using var db = Database.Context();
        var guestId = (await db.Bookings.SingleAsync(b => b.Id == new BookingId(created.BookingId), Ct)).GuestId!.Value;
        var row = await db.Outbox.SingleAsync(o => o.AggregateId == created.BookingId, Ct);
        Assert.Equal("BookingCreated", row.EventType);
        Assert.Equal(correlationId, row.CorrelationId);
        var actor = row.Envelope.RootElement.GetProperty("actor");
        Assert.Equal("guest", actor.GetProperty("type").GetString());
        Assert.Equal(guestId.Value.ToString(), actor.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Without_a_correlation_header_a_new_one_is_made_and_returned()
    {
        var response = await PostAsync(Client, Venue + Path, BookingBody(Tomorrow, 19, 0, 2));
        var created = await ReadAsync<CreateBookingResponse>(response, HttpStatusCode.Created);

        var header = Guid.Parse(response.Headers.GetValues("X-Correlation-Id").Single());
        await using var db = Database.Context();
        var row = await db.Outbox.SingleAsync(o => o.AggregateId == created.BookingId, Ct);
        Assert.Equal(header, row.CorrelationId);
        Assert.Equal(JsonValueKind.Object, row.Envelope.RootElement.ValueKind);
    }
}
