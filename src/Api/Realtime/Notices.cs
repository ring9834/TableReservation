using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;

namespace Reservation.Api.Realtime;

// Design §15: the server → client notices. They carry IDs only, never personal data (ADR-002); the screen refetches.
// Published as OpenAPI component schemas (NoticeSchemasTransformer) so orval generates them (ADR-004).

/// <summary>Invalidate the <c>floor-plan</c> and <c>bookings</c> queries.</summary>
/// <param name="TableIds">Tables named by the event; empty when the event names none (the screen refetches the whole plan).</param>
/// <param name="BookingIds">The booking that changed.</param>
/// <param name="Sequence">The event's sequence within its aggregate (ADR-002 envelope).</param>
internal sealed record FloorPlanChangedNotice(IReadOnlyList<Guid> TableIds, IReadOnlyList<Guid> BookingIds, long Sequence);

/// <summary>
/// Invalidate <c>waitlist</c>; highlight the suggestion. Waitlist events send their entry with no suggestion; the
/// <c>WaitlistSuggester</c> sends the suggested entry and the freed table when a table frees (M6a, AC4.2); the overdue
/// job sends entries that just became overdue (AC4.6).
/// </summary>
internal sealed record WaitlistChangedNotice(IReadOnlyList<Guid> EntryIds, Guid? SuggestedEntryId, Guid? FreedTableId);

/// <summary>Invalidate <c>messages</c> (phone panel).</summary>
internal sealed record MessageLogChangedNotice(IReadOnlyList<Guid> MessageIds);

/// <summary>Invalidate <c>pos-messages</c> (POS log).</summary>
internal sealed record PosLogChangedNotice(IReadOnlyList<Guid> PosMessageIds);

/// <summary>A notice and the client method that receives it.</summary>
internal sealed record RealtimeNotice(string Method, object Payload);

/// <summary>Client method names (Design §15).</summary>
internal static class NoticeMethods
{
    public const string FloorPlanChanged = "floorPlanChanged";
    public const string WaitlistChanged = "waitlistChanged";
    public const string MessageLogChanged = "messageLogChanged";
    public const string PosLogChanged = "posLogChanged";
}

/// <summary>Which notice each domain event produces (Design §4.3 "Realtime" column, §15.1).</summary>
internal static class NoticeMap
{
    public static RealtimeNotice? For(IDomainEvent domainEvent, long sequence) => domainEvent switch
    {
        BookingCreated e => FloorPlan(e.TableIds, e.BookingId, sequence),
        DepositCaptured e => FloorPlan([], e.BookingId, sequence),
        BookingConfirmed e => FloorPlan([], e.BookingId, sequence),
        BookingCancelled e => FloorPlan([], e.BookingId, sequence),
        DepositRefunded e => FloorPlan([], e.BookingId, sequence),
        BookingSeated e => FloorPlan(e.TableIds, e.BookingId, sequence),
        BookingCompleted e => FloorPlan([], e.BookingId, sequence),
        TableCleared e => FloorPlan(e.TableIds, e.BookingId, sequence),
        BookingBillAttached e => new RealtimeNotice(NoticeMethods.PosLogChanged, new PosLogChangedNotice([e.PosMessageId.Value])),
        WaitlistJoined e => Waitlist(e.WaitlistEntryId),
        WaitlistNotified e => Waitlist(e.WaitlistEntryId),
        WaitlistEnded e => Waitlist(e.WaitlistEntryId),
        MessageSent e => new RealtimeNotice(NoticeMethods.MessageLogChanged, new MessageLogChangedNotice([e.MessageId.Value])),
        _ => null,
    };

    private static RealtimeNotice FloorPlan(IReadOnlyList<TableId> tableIds, BookingId bookingId, long sequence) =>
        new(NoticeMethods.FloorPlanChanged, new FloorPlanChangedNotice([.. tableIds.Select(t => t.Value)], [bookingId.Value], sequence));

    private static RealtimeNotice Waitlist(WaitlistEntryId entryId) =>
        new(NoticeMethods.WaitlistChanged, new WaitlistChangedNotice([entryId.Value], null, null));
}
