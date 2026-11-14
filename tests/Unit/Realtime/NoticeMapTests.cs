using System.Text.Json;
using Reservation.Api.Realtime;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;

namespace Reservation.Tests.Unit.Realtime;

/// <summary>Design §4.3 "Realtime" column and §15.1: every event becomes one ID-only notice.</summary>
public sealed class NoticeMapTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 24, 5, 25, 0, TimeSpan.Zero);
    private static readonly VenueId _venue = new(Guid.NewGuid());
    private static readonly BookingId _booking = new(Guid.NewGuid());
    private static readonly TableId _table = new(Guid.NewGuid());
    private static readonly WaitlistEntryId _entry = new(Guid.NewGuid());

    public static TheoryData<string> BookingEventsWithoutTables => ["DepositCaptured", "BookingConfirmed", "BookingCancelled", "DepositRefunded", "BookingCompleted", "LatePaymentReceived"];

    [Fact]
    public void Every_catalogue_event_maps_to_a_notice()
    {
        foreach (var domainEvent in AllEvents())
        {
            Assert.NotNull(NoticeMap.For(domainEvent, 1));
        }

        Assert.Equal(new Infrastructure.Outbox.EventTypeRegistry().All.Count, AllEvents().Select(e => e.GetType()).Distinct().Count());
    }

    [Fact]
    public void BookingCreated_names_its_tables_booking_and_sequence()
    {
        var notice = NoticeMap.For(Created(), 1)!;

        Assert.Equal("floorPlanChanged", notice.Method);
        var payload = Assert.IsType<FloorPlanChangedNotice>(notice.Payload);
        Assert.Equal([_table.Value], payload.TableIds);
        Assert.Equal([_booking.Value], payload.BookingIds);
        Assert.Equal(1, payload.Sequence);
    }

    [Fact]
    public void BookingSeated_and_TableCleared_name_their_tables()
    {
        var seated = (FloorPlanChangedNotice)NoticeMap.For(Seated(), 2)!.Payload;
        var cleared = (FloorPlanChangedNotice)NoticeMap.For(new TableCleared(Guid.NewGuid(), _now, _venue, _booking, [_table], _now), 3)!.Payload;

        Assert.Equal([_table.Value], seated.TableIds);
        Assert.Equal(2, seated.Sequence);
        Assert.Equal([_table.Value], cleared.TableIds);
        Assert.Equal(3, cleared.Sequence);
    }

    [Theory]
    [MemberData(nameof(BookingEventsWithoutTables))]
    public void Booking_events_without_tables_send_an_empty_table_list(string eventType)
    {
        var domainEvent = AllEvents().Single(e => e.GetType().Name == eventType);

        var notice = NoticeMap.For(domainEvent, 4)!;

        Assert.Equal("floorPlanChanged", notice.Method);
        var payload = (FloorPlanChangedNotice)notice.Payload;
        Assert.Empty(payload.TableIds);
        Assert.Equal([_booking.Value], payload.BookingIds);
    }

    [Fact]
    public void Waitlist_message_and_bill_events_use_their_own_notices()
    {
        var joined = NoticeMap.For(new WaitlistJoined(Guid.NewGuid(), _now, _venue, _entry, new GuestId(Guid.NewGuid()), 4, 20), 1)!;
        var message = new MessageId(Guid.NewGuid());
        var sent = NoticeMap.For(new MessageSent(Guid.NewGuid(), _now, _venue, message, MessagePurpose.TableReady, MessageChannel.Sms, _entry.Value, "+64 *** **0001"), 1)!;
        var posMessage = new PosMessageId(Guid.NewGuid());
        var bill = NoticeMap.For(new BookingBillAttached(Guid.NewGuid(), _now, _venue, _booking, "B-1", 12000, posMessage, BillMatchedBy.Manual, _now), 5)!;

        Assert.Equal("waitlistChanged", joined.Method);
        var waitlist = Assert.IsType<WaitlistChangedNotice>(joined.Payload);
        Assert.Equal([_entry.Value], waitlist.EntryIds);
        Assert.Null(waitlist.SuggestedEntryId);
        Assert.Null(waitlist.FreedTableId);
        Assert.Equal("messageLogChanged", sent.Method);
        Assert.Equal([message.Value], ((MessageLogChangedNotice)sent.Payload).MessageIds);
        Assert.Equal("posLogChanged", bill.Method);
        Assert.Equal([posMessage.Value], ((PosLogChangedNotice)bill.Payload).PosMessageIds);
    }

    [Fact]
    public void Notices_carry_ids_and_sequence_only()
    {
        foreach (var domainEvent in AllEvents())
        {
            var notice = NoticeMap.For(domainEvent, 1)!;
            var json = JsonSerializer.SerializeToElement(notice.Payload, notice.Payload.GetType(), JsonSerializerOptions.Web);

            foreach (var property in json.EnumerateObject())
            {
                Assert.True(
                    property.Name == "sequence" || property.Name.EndsWith("Ids", StringComparison.Ordinal) || property.Name.EndsWith("Id", StringComparison.Ordinal),
                    $"{domainEvent.GetType().Name} → {notice.Method} has a field {property.Name}");
            }

            Assert.DoesNotContain("+64", json.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static BookingCreated Created() => new(
        Guid.NewGuid(), _now, _venue, _booking, new GuestId(Guid.NewGuid()), "GD2A4K", BookingChannel.Online, 2,
        _now.AddHours(1), _now.AddHours(2), [_table], null, null);

    private static BookingSeated Seated() => new(Guid.NewGuid(), _now, _venue, _booking, [_table], 2, SeatSource.Booking, null, _now);

    private static IEnumerable<IDomainEvent> AllEvents() =>
    [
        Created(),
        new DepositCaptured(Guid.NewGuid(), _now, _venue, _booking, "pi_1", 12000),
        new LatePaymentReceived(Guid.NewGuid(), _now, _venue, _booking, "pi_1", 12000),
        new BookingConfirmed(Guid.NewGuid(), _now, _venue, _booking, Actor.ForSystem()),
        new BookingCancelled(Guid.NewGuid(), _now, _venue, _booking, CancelReason.GuestRequest, Actor.ForSystem(), RefundDecision.Full),
        new DepositRefunded(Guid.NewGuid(), _now, _venue, _booking, "re_1", 12000),
        Seated(),
        new BookingCompleted(Guid.NewGuid(), _now, _venue, _booking, CompletionSource.BillClosed, "B-1", 12000, _now),
        new TableCleared(Guid.NewGuid(), _now, _venue, _booking, [_table], _now),
        new BookingBillAttached(Guid.NewGuid(), _now, _venue, _booking, "B-1", 12000, new PosMessageId(Guid.NewGuid()), BillMatchedBy.LateMatch, _now),
        new WaitlistJoined(Guid.NewGuid(), _now, _venue, _entry, new GuestId(Guid.NewGuid()), 4, 20),
        new WaitlistNotified(Guid.NewGuid(), _now, _venue, _entry, _table, _now),
        new WaitlistEnded(Guid.NewGuid(), _now, _venue, _entry, WaitlistOutcome.Seated, _booking),
        new MessageSent(Guid.NewGuid(), _now, _venue, new MessageId(Guid.NewGuid()), MessagePurpose.Confirmation, MessageChannel.Sms, _booking.Value, "+64 *** **0001"),
    ];
}
