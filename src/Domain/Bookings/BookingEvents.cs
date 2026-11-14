using Reservation.Domain.Common;

namespace Reservation.Domain.Bookings;

// Design §4.2: events raised by the Booking aggregate (v1).

[EventMetadata("BookingCreated", Version = 1, AggregateType = "Booking")]
public sealed record BookingCreated(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, GuestId? GuestId, string Reference, BookingChannel Channel, int PartySize,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<TableId> TableIds,
    long? DepositCents, DateTimeOffset? HoldExpiresAt)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("DepositCaptured", Version = 1, AggregateType = "Booking")]
public sealed record DepositCaptured(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, string PaymentIntentId, long AmountCents)
    : DomainEvent(EventId, OccurredAt, VenueId);

/// <summary>A payment arrived after the hold expiry had cancelled the booking; it is refunded in full (M8a, Design §8.4).</summary>
[EventMetadata("LatePaymentReceived", Version = 1, AggregateType = "Booking")]
public sealed record LatePaymentReceived(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, string PaymentIntentId, long AmountCents)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("BookingConfirmed", Version = 1, AggregateType = "Booking")]
public sealed record BookingConfirmed(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, Actor ConfirmedBy)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("BookingCancelled", Version = 1, AggregateType = "Booking")]
public sealed record BookingCancelled(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, CancelReason Reason, Actor CancelledBy, RefundDecision RefundDecision)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("DepositRefunded", Version = 1, AggregateType = "Booking")]
public sealed record DepositRefunded(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, string RefundId, long AmountCents)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("BookingSeated", Version = 1, AggregateType = "Booking")]
public sealed record BookingSeated(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, IReadOnlyList<TableId> TableIds, int PartySize,
    SeatSource Source, WaitlistEntryId? WaitlistEntryId, DateTimeOffset SeatedAt)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("BookingCompleted", Version = 1, AggregateType = "Booking")]
public sealed record BookingCompleted(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, CompletionSource Source, string? PosBillId, long? BillTotalCents, DateTimeOffset ClosedAt)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("TableCleared", Version = 1, AggregateType = "Booking")]
public sealed record TableCleared(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, IReadOnlyList<TableId> TableIds, DateTimeOffset ClearedAt)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("BookingBillAttached", Version = 1, AggregateType = "Booking")]
public sealed record BookingBillAttached(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    BookingId BookingId, string PosBillId, long BillTotalCents, PosMessageId PosMessageId,
    BillMatchedBy MatchedBy, DateTimeOffset AttachedAt)
    : DomainEvent(EventId, OccurredAt, VenueId);
