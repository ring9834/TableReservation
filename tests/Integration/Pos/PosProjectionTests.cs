using Reservation.Contracts.Pos;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// Design §6.3 through the API: which events queue which POS messages, with which keys and envelope (AC6.3, AC6.4),
/// once per event; the projection never calls the POS.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PosProjectionTests(PostgresFixture postgres) : PosTestBase(postgres)
{
    [Fact]
    public async Task A_booking_with_a_guest_queues_UpsertMember_with_the_envelope()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Aroha Ngata", "021 011 0291");
        await DispatchAsync();

        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosDirection.Outbound, PosMessageTypes.UpsertMember, PosMessageStatus.Waiting, 1L, 0),
            (row.Direction, row.MessageType, row.Status, row.VenueSequence, row.Attempts));
        Assert.Equal(booking.BookingId, row.BookingId?.Value);
        Assert.Equal(DemoTime.Now, row.CreatedAt);
        Assert.Equal(Infra.GetUtcNow(), row.NextAttemptAt);
        Assert.Empty(Adapter.Calls); // queued only, never sent by the projection

        var payload = row.Payload.RootElement;
        Assert.Equal(1, payload.GetProperty("version").GetInt32());
        Assert.Equal(row.Id.Value, payload.GetProperty("messageId").GetGuid());
        Assert.Equal("UpsertMember", payload.GetProperty("messageType").GetString());
        Assert.Equal(VenueId, payload.GetProperty("venueId").GetGuid());
        Assert.Equal(booking.BookingId, payload.GetProperty("bookingId").GetGuid());
        Assert.Equal(0, payload.GetProperty("tableIds").GetArrayLength());
        Assert.Equal(DemoTime.Now, payload.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("+64210110291", payload.GetProperty("memberPhone").GetString());
        Assert.Equal("Aroha Ngata", payload.GetProperty("name").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());

        var eventId = await EventIdAsync(booking.BookingId, "BookingCreated");
        Assert.Equal($"{eventId}:UpsertMember", row.IdempotencyKey);
        Assert.Equal(eventId, row.SourceEventId);
    }

    [Fact]
    public async Task A_walk_in_without_a_guest_queues_only_OpenTable()
    {
        var walkIn = await WalkInAsync("T1", name: "Sam");
        await DispatchAsync();

        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal(PosMessageTypes.OpenTable, row.MessageType);
        var payload = row.Payload.RootElement;
        Assert.Equal([Table("T1")], payload.GetProperty("tableIds").EnumerateArray().Select(t => t.GetGuid()));
        Assert.Equal(walkIn.BookingId, payload.GetProperty("bookingId").GetGuid());
        Assert.Equal(2, payload.GetProperty("partySize").GetInt32());
        Assert.Equal("Sam", payload.GetProperty("guestName").GetString());
        Assert.Equal(DemoTime.Now, payload.GetProperty("seatedAt").GetDateTimeOffset());
        // As stored and sent: UTC ending in Z, as docs/api/pos-messages.md says (M11b), never "+00:00".
        Assert.EndsWith("Z", payload.GetProperty("occurredAt").GetString());
        Assert.EndsWith("Z", payload.GetProperty("seatedAt").GetString());
        Assert.Equal($"{await EventIdAsync(walkIn.BookingId, "BookingSeated")}:OpenTable", row.IdempotencyKey);
    }

    [Fact]
    public async Task Seating_a_booking_with_a_captured_deposit_queues_OpenTable_then_ApplyDeposit()
    {
        var bookingId = await BookingIdAsync("GD8G4S"); // seed #7: deposit captured, T7
        await SeatAsync(bookingId);
        await DispatchAsync();

        var rows = await PosRowsAsync();
        Assert.Equal([PosMessageTypes.OpenTable, PosMessageTypes.ApplyDeposit], rows.Select(r => r.MessageType));
        Assert.Equal([1L, 2L], rows.Select(r => r.VenueSequence));
        Assert.All(rows, r => Assert.Equal(bookingId, r.BookingId?.Value));

        var deposit = rows[1].Payload.RootElement;
        Assert.Equal([Table("T7")], deposit.GetProperty("tableIds").EnumerateArray().Select(t => t.GetGuid()));
        Assert.Equal(12_000, deposit.GetProperty("amountCents").GetInt64());
        Assert.Equal("NZD", deposit.GetProperty("currency").GetString());
        Assert.StartsWith("pi_", deposit.GetProperty("paymentRef").GetString(), StringComparison.Ordinal);

        var eventId = await EventIdAsync(bookingId, "BookingSeated");
        Assert.Equal([$"{eventId}:OpenTable", $"{eventId}:ApplyDeposit"], rows.Select(r => r.IdempotencyKey));
    }

    [Fact]
    public async Task Seating_a_booking_without_a_deposit_queues_no_ApplyDeposit()
    {
        await SeatAsync(await BookingIdAsync("GD7F3R")); // seed #6: no deposit, T5
        await DispatchAsync();

        Assert.Equal([PosMessageTypes.OpenTable], (await PosRowsAsync()).Select(r => r.MessageType));
    }

    [Fact]
    public async Task Each_event_delivered_twice_queues_its_messages_once()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Aroha Ngata", "021 011 0291");
        await DispatchAsync();
        await SeatAsync(booking.BookingId);
        await DispatchAsync();
        var deposit = await BookingIdAsync("GD8G4S");
        await SeatAsync(deposit);
        await DispatchAsync();

        await RedeliverAsync(booking.BookingId, "BookingCreated");
        await RedeliverAsync(booking.BookingId, "BookingSeated");
        await RedeliverAsync(deposit, "BookingSeated");

        var rows = await PosRowsAsync();
        Assert.Equal(
            [PosMessageTypes.UpsertMember, PosMessageTypes.OpenTable, PosMessageTypes.OpenTable, PosMessageTypes.ApplyDeposit],
            rows.Select(r => r.MessageType));
        Assert.Equal([1L, 2L, 3L, 4L], rows.Select(r => r.VenueSequence));
    }

    [Fact]
    public async Task The_seed_creates_no_POS_messages()
    {
        await DispatchAsync();
        Assert.Empty(await PosRowsAsync());
    }

    private async Task<Guid> EventIdAsync(Guid bookingId, string eventType)
    {
        await using var db = Database.Context();
        return db.Outbox.Where(o => o.AggregateId == bookingId && o.EventType == eventType).Select(o => o.EventId).Single();
    }
}
