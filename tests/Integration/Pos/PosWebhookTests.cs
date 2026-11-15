using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Pos;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// Inbound <c>BillClosed</c> (Design §5.5, §6.5, §6.6, §6.7; AC6.6): the signed webhook and the demo's "Bill paid" take
/// the same path (signature, inbox, CloseBill); the four matching steps, second bills, ambiguity and repeats; the
/// guest's visit and spend after dispatch.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PosWebhookTests(PostgresFixture postgres) : PosTestBase(postgres)
{
    [Fact]
    public async Task A_bad_missing_or_stale_signature_is_401_and_saves_nothing()
    {
        var body = Json(Bill("T4", 5_000));

        var wrong = await PostWebhookAsync(body, PosWebhookSigner.Sign(body, "not-the-secret", Infra.GetUtcNow()));
        var missing = await PostWebhookAsync(body, sign: false);
        var stale = await PostWebhookAsync(body, PosWebhookSigner.Sign(body, ApiFactory.PosWebhookSecret, Infra.GetUtcNow().AddMinutes(-10)));
        var edited = await PostWebhookAsync(body.Replace("5000", "5", StringComparison.Ordinal), PosWebhookSigner.Sign(body, ApiFactory.PosWebhookSecret, Infra.GetUtcNow()));

        foreach (var response in new[] { wrong, missing, stale, edited })
        {
            await ProblemAssert.IsAsync(response, HttpStatusCode.Unauthorized, "invalid_signature");
        }

        Assert.Empty(await PosRowsAsync());
        Assert.Equal(0, await InboxRowsAsync());
    }

    [Fact]
    public async Task A_signed_body_that_is_not_a_BillClosed_is_400()
    {
        var response = await PostWebhookAsync("""{"messageType":"Hello"}""");
        await ProblemAssert.IsAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Empty(await PosRowsAsync());
    }

    [Fact]
    public async Task A_repeated_message_is_processed_once()
    {
        var body = Json(Bill("T4", 5_000));

        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync(body)).StatusCode);

        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosDirection.Inbound, PosMessageStatus.Matched), (row.Direction, row.Status));
        Assert.Equal(1, await InboxRowsAsync());
    }

    [Fact]
    public async Task Step1_the_echoed_booking_id_completes_the_seated_booking()
    {
        var walkIn = await WalkInAsync("T1");
        await DispatchAsync();
        await DeliverPosAsync(); // the mock now has T1 open for this booking

        var result = await BillPaidAsync("T1", 8_450);

        Assert.Equal((PosMessageStatus.Matched, walkIn.BookingId), (result.Status, result.BookingId));
        var booking = await BookingAsync(walkIn.BookingId);
        Assert.Equal((BookingStatus.Completed, CompletionSource.BillClosed), (booking.Status, booking.CompletedBy));
        Assert.Equal(Money.FromNzd(8_450), booking.BillTotal);
        Assert.Equal(result.PosMessageId, Assert.Single(booking.Bills).SourceMessage.Value);
        var inbound = (await PosRowsAsync()).Last();
        Assert.Equal(walkIn.BookingId, inbound.Payload.RootElement.GetProperty("bookingId").GetGuid());
        Assert.Equal(["BookingCompleted", "TableCleared"], await NewEventsAsync(walkIn.BookingId, after: "BookingSeated"));
    }

    [Fact]
    public async Task Step2_bill_paid_on_a_seeded_table_the_POS_never_opened_completes_the_seated_booking()
    {
        var bookingId = await BookingIdAsync("GD2A4K"); // seed #1, seated on T4; the seed sends nothing to the POS
        var guestBefore = await GuestOfAsync(bookingId);

        var result = await BillPaidAsync("T4", 18_500);
        await DispatchAsync();

        Assert.Equal((PosMessageStatus.Matched, bookingId), (result.Status, result.BookingId));
        var inbound = Assert.Single(await PosRowsAsync());
        Assert.False(inbound.Payload.RootElement.TryGetProperty("bookingId", out _)); // no echo: step 2 matched by table
        var booking = await BookingAsync(bookingId);
        Assert.Equal((BookingStatus.Completed, CompletionSource.BillClosed), (booking.Status, booking.CompletedBy));

        var guest = await GuestOfAsync(bookingId);
        Assert.Equal(guestBefore.VisitCount + 1, guest.VisitCount);
        Assert.Equal(Money.FromNzd(18_500), guest.LastBillTotal);
        Assert.Equal(DemoTime.Now, guest.LastVisitAt);
    }

    [Fact]
    public async Task Step3_a_bill_after_the_host_cleared_is_attached_late_and_the_visit_counts_once()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Mere Tane", "021 011 0381");
        Assert.Equal([Table("T1")], booking.TableIds);
        await SeatAsync(booking.BookingId);
        await ClearAsync("T1", booking.BookingId);
        await DispatchAsync();
        var cleared = await GuestOfAsync(booking.BookingId);
        Assert.Equal((1, null), (cleared.VisitCount, cleared.LastBillTotal)); // the visit counts at the clear

        var response = await PostWebhookAsync(Json(Bill("T1", 6_200, closedAt: DemoTime.Now.AddMinutes(10))));
        await DispatchAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inbound = (await PosRowsAsync()).Last();
        Assert.Equal((PosMessageStatus.Matched, booking.BookingId), (inbound.Status, inbound.BookingId?.Value));
        var attached = await BookingAsync(booking.BookingId);
        Assert.Equal((CompletionSource.ClearedByHost, Money.FromNzd(6_200)), (attached.CompletedBy, attached.BillTotal));
        Assert.Equal(["BookingBillAttached"], await NewEventsAsync(booking.BookingId, after: "TableCleared"));

        var guest = await GuestOfAsync(booking.BookingId);
        Assert.Equal((1, Money.FromNzd(6_200)), (guest.VisitCount, guest.LastBillTotal));
        Assert.Contains(inbound.Id.Value, PosLog.Ids);
    }

    [Fact]
    public async Task Bill_paid_through_the_mock_after_a_clear_attaches_by_the_echoed_booking_with_one_visit()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Mere Tane", "021 011 0381");
        Assert.Equal([Table("T1")], booking.TableIds);
        await SeatAsync(booking.BookingId);
        await DispatchAsync();
        await DeliverPosAsync();
        await ClearAsync("T1", booking.BookingId);
        await DispatchAsync();

        var result = await BillPaidAsync("T1", 7_700);
        await DispatchAsync();

        Assert.Equal((PosMessageStatus.Matched, booking.BookingId), (result.Status, result.BookingId));
        var guest = await GuestOfAsync(booking.BookingId);
        Assert.Equal((1, Money.FromNzd(7_700)), (guest.VisitCount, guest.LastBillTotal));
    }

    [Fact]
    public async Task Bill_paid_end_to_end_counts_one_visit_with_the_total()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Mere Tane", "021 011 0381");
        Assert.Equal([Table("T1")], booking.TableIds);
        await SeatAsync(booking.BookingId);
        await DispatchAsync();
        await DeliverPosAsync();

        await BillPaidAsync("T1", 9_900);
        await DispatchAsync();
        await RedeliverAsync(booking.BookingId, "BookingCompleted");

        var guest = await GuestOfAsync(booking.BookingId);
        Assert.Equal((1, Money.FromNzd(9_900), DemoTime.Now), (guest.VisitCount, guest.LastBillTotal, guest.LastVisitAt));
    }

    [Fact]
    public async Task Step4_a_bill_for_a_table_with_no_booking_is_unmatched()
    {
        var result = await BillPaidAsync("T3", 2_500);

        Assert.Equal((PosMessageStatus.Unmatched, null, "no_match"), (result.Status, result.BookingId, result.UnmatchedReason));
        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosMessageStatus.Unmatched, "no_match"), (row.Status, row.UnmatchedReason));
    }

    [Fact]
    public async Task A_late_bill_outside_the_window_is_unmatched()
    {
        var walkIn = await WalkInAsync("T1");
        await ClearAsync("T1", walkIn.BookingId);

        await PostWebhookAsync(Json(Bill("T1", 3_000, closedAt: DemoTime.Now.AddMinutes(31))));

        Assert.Equal("no_match", (await PosRowsAsync()).Last().UnmatchedReason);
        Assert.Null((await BookingAsync(walkIn.BookingId)).BillTotal);
    }

    [Fact]
    public async Task Two_cleared_bookings_at_the_table_make_a_bill_without_booking_id_ambiguous()
    {
        var first = await WalkInAsync("T1");
        await ClearAsync("T1", first.BookingId);
        var second = await WalkInAsync("T1");
        await ClearAsync("T1", second.BookingId);

        await PostWebhookAsync(Json(Bill("T1", 4_000)));

        var row = (await PosRowsAsync()).Last();
        Assert.Equal((PosMessageStatus.Unmatched, "ambiguous", null), (row.Status, row.UnmatchedReason, row.BookingId));
        Assert.Null((await BookingAsync(first.BookingId)).BillTotal);
        Assert.Null((await BookingAsync(second.BookingId)).BillTotal);
    }

    [Fact]
    public async Task A_different_second_bill_for_a_booking_is_unmatched_second_bill()
    {
        var walkIn = await WalkInAsync("T1");
        await PostWebhookAsync(Json(Bill("T1", 5_000, bookingId: walkIn.BookingId, posBillId: "bill-1")));

        await PostWebhookAsync(Json(Bill("T1", 1_200, bookingId: walkIn.BookingId, posBillId: "bill-2")));

        var row = (await PosRowsAsync()).Last();
        Assert.Equal((PosMessageStatus.Unmatched, "second_bill"), (row.Status, row.UnmatchedReason));
        Assert.Equal(Money.FromNzd(5_000), (await BookingAsync(walkIn.BookingId)).BillTotal);
    }

    [Fact]
    public async Task A_repeated_bill_id_in_a_new_message_changes_nothing()
    {
        var walkIn = await WalkInAsync("T1");
        await PostWebhookAsync(Json(Bill("T1", 5_000, posBillId: "bill-1")));
        await DispatchAsync();

        var response = await PostWebhookAsync(Json(Bill("T1", 5_000, posBillId: "bill-1")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await PosRowsAsync();
        Assert.Equal(2, rows.Count(r => r.Direction == PosDirection.Inbound));
        Assert.All(rows.Where(r => r.Direction == PosDirection.Inbound), r => Assert.Equal((PosMessageStatus.Matched, walkIn.BookingId), (r.Status, r.BookingId?.Value)));
        Assert.Single((await BookingAsync(walkIn.BookingId)).Bills);
        Assert.Empty(await NewEventsAsync(walkIn.BookingId, after: "TableCleared"));
    }

    [Fact]
    public async Task Inbound_messages_take_the_next_venue_sequence()
    {
        await WalkInAsync("T1");
        await DispatchAsync();

        var result = await BillPaidAsync("T1", 1_000);

        Assert.Equal([1L, 2L], (await PosRowsAsync()).Select(r => r.VenueSequence));
        Assert.Equal(2, (await PosRowsAsync()).Single(r => r.Id.Value == result.PosMessageId).VenueSequence);
    }

    [Fact]
    public async Task PosLogChanged_for_an_inbound_bill_comes_after_the_commit()
    {
        var result = await BillPaidAsync("T4", 1_000);

        Assert.Equal([result.PosMessageId], PosLog.Ids);
    }

    [Fact]
    public async Task Bill_paid_needs_a_table_of_the_session_venue_and_a_positive_total()
    {
        var unknown = await PostAsync(Client, "/api/v1/demo/pos/bill-paid", new { tableId = Guid.NewGuid(), totalCents = 1_000 });
        await ProblemAssert.IsAsync(unknown, HttpStatusCode.NotFound, "not_found");

        var zero = await PostAsync(Client, "/api/v1/demo/pos/bill-paid", new { tableId = Table("T4"), totalCents = 0 });
        await ProblemAssert.IsAsync(zero, HttpStatusCode.BadRequest, "validation_failed");

        var anonymous = await PostAsync(NewClient(), "/api/v1/demo/pos/bill-paid", new { tableId = Table("T4"), totalCents = 1_000 });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(await PosRowsAsync());
    }

    private async Task<int> InboxRowsAsync()
    {
        await using var db = Database.Context();
        return await db.Inbox.CountAsync(i => i.Consumer == PosWebhookHandler.Consumer, Ct);
    }

    private async Task<Booking> BookingAsync(Guid bookingId)
    {
        await using var db = Database.Context();
        var id = new BookingId(bookingId);
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == id, Ct);
    }

    private async Task<Guest> GuestOfAsync(Guid bookingId)
    {
        var booking = await BookingAsync(bookingId);
        await using var db = Database.Context();
        return await db.Guests.AsNoTracking().SingleAsync(g => g.Id == booking.GuestId, Ct);
    }

    /// <summary>The booking's event types raised after the last <paramref name="after"/>, in order.</summary>
    private async Task<List<string>> NewEventsAsync(Guid bookingId, string after)
    {
        await using var db = Database.Context();
        var events = await db.Outbox.AsNoTracking().Where(o => o.AggregateId == bookingId).OrderBy(o => o.Sequence)
            .Select(o => o.EventType).ToListAsync(Ct);
        return [.. events.Skip(events.LastIndexOf(after) + 1)];
    }
}
