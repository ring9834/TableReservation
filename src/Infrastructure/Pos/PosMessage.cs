using System.Text.Json;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Pos;

public enum PosDirection
{
    Outbound,
    Inbound,
}

/// <summary>Outbound: Waiting → Sent → Acknowledged / Failed. Inbound: Received → Matched / Unmatched → Dismissed (§3.5).</summary>
public enum PosMessageStatus
{
    Waiting,
    Sent,
    Acknowledged,
    Failed,
    Received,
    Matched,
    Unmatched,
    Dismissed,
}

/// <summary>
/// One row of <c>pos_messages</c>, for both directions (Design §6.1, §6.7, §13). A delivery queue item, not an
/// aggregate: it raises no domain events. Two clocks (§5.6): <see cref="CreatedAt"/> and <see cref="DeliveredAt"/> are
/// business-clock times people see in the POS log; <see cref="QueuedAt"/> and <see cref="NextAttemptAt"/> are
/// infrastructure-clock times the delivery worker schedules by.
/// </summary>
public sealed class PosMessage
{
    /// <summary>Last error of a message the POS was unavailable for.</summary>
    public const string Unavailable = "pos_unavailable";

    /// <summary>Failure reason when the POS stayed unavailable for the whole window (24 h).</summary>
    public const string UnavailableTooLong = "unavailable_24h";

    public PosMessage(
        PosMessageId id, VenueId venueId, PosDirection direction, string messageType, string idempotencyKey,
        Guid? sourceEventId, JsonDocument payload, PosMessageStatus status, long venueSequence, DateTimeOffset createdAt)
    {
        Id = id;
        VenueId = venueId;
        Direction = direction;
        MessageType = messageType;
        IdempotencyKey = idempotencyKey;
        SourceEventId = sourceEventId;
        Payload = payload;
        Status = status;
        VenueSequence = venueSequence;
        CreatedAt = createdAt;
        QueuedAt = createdAt;
    }

    public PosMessageId Id { get; private set; }

    public VenueId VenueId { get; private set; }

    public PosDirection Direction { get; private set; }

    public string MessageType { get; private set; }

    public string IdempotencyKey { get; private set; }

    public Guid? SourceEventId { get; private set; }

    public JsonDocument Payload { get; private set; }

    public PosMessageStatus Status { get; private set; }

    public long VenueSequence { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Business clock: when the message was created (shown in the POS log).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Business clock: when the POS accepted an outbound message.</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>The booking the message is about; for an inbound bill, the booking it was matched to (§6.5).</summary>
    public BookingId? BookingId { get; private set; }

    /// <summary>Inbound: <c>no_match</c>, <c>ambiguous</c> or <c>second_bill</c> (§6.5, §6.6).</summary>
    public string? UnmatchedReason { get; private set; }

    /// <summary>Infrastructure clock: when the row was written; the unavailable and retry windows count from here.</summary>
    public DateTimeOffset QueuedAt { get; private set; }

    /// <summary>Infrastructure clock: when the worker may try an outbound message next; <c>null</c> for inbound rows.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>A code only (e.g. <c>pos_unavailable</c>, <c>adapter_exception:TimeoutException</c>), never a payload.</summary>
    public string? LastError { get; private set; }

    /// <summary>A message to the POS, waiting for the delivery worker.</summary>
    public static PosMessage Outbound(
        PosMessageId id, VenueId venueId, string messageType, string idempotencyKey, Guid sourceEventId, BookingId bookingId,
        JsonDocument payload, long venueSequence, DateTimeOffset createdAt, DateTimeOffset queuedAt) =>
        new(id, venueId, PosDirection.Outbound, messageType, idempotencyKey, sourceEventId, payload, PosMessageStatus.Waiting, venueSequence, createdAt)
        {
            BookingId = bookingId,
            QueuedAt = queuedAt,
            NextAttemptAt = queuedAt,
        };

    /// <summary>A message from the POS, received and about to be matched.</summary>
    public static PosMessage Inbound(
        PosMessageId id, VenueId venueId, string messageType, string idempotencyKey, JsonDocument payload, long venueSequence,
        DateTimeOffset createdAt, DateTimeOffset queuedAt) =>
        new(id, venueId, PosDirection.Inbound, messageType, idempotencyKey, null, payload, PosMessageStatus.Received, venueSequence, createdAt)
        {
            QueuedAt = queuedAt,
        };

    /// <summary>The POS accepted the message (a repeat it ignored counts too).</summary>
    public void MarkSent(DateTimeOffset at)
    {
        RequireOutbound(PosMessageStatus.Waiting);
        Attempts++;
        Status = PosMessageStatus.Sent;
        DeliveredAt = at;
        NextAttemptAt = null;
        LastError = null;
    }

    /// <summary>
    /// The POS is unavailable: try again at <paramref name="next"/> without counting an attempt (§6.4), until the POS has
    /// been unavailable for longer than <paramref name="window"/> since the message was queued; then Failed.
    /// </summary>
    public void DeferUnavailable(DateTimeOffset now, DateTimeOffset next, TimeSpan window)
    {
        RequireOutbound(PosMessageStatus.Waiting);
        if (now - QueuedAt > window)
        {
            Fail(UnavailableTooLong);
            return;
        }

        NextAttemptAt = next;
        LastError = Unavailable;
    }

    /// <summary>A temporary error: one more attempt, retried at <paramref name="next"/>.</summary>
    public void ScheduleRetry(string error, DateTimeOffset next)
    {
        RequireOutbound(PosMessageStatus.Waiting);
        Attempts++;
        NextAttemptAt = next;
        LastError = error;
    }

    /// <summary>The POS refused the message, or retries ran out. The venue's queue stops behind it (§6.7).</summary>
    public void Fail(string reason)
    {
        RequireOutbound(PosMessageStatus.Waiting);
        Status = PosMessageStatus.Failed;
        NextAttemptAt = null;
        LastError = reason;
    }

    /// <summary>An inbound bill matched to a booking (completed it, attached late, or already on it).</summary>
    public void Match(BookingId bookingId)
    {
        RequireInbound();
        Status = PosMessageStatus.Matched;
        BookingId = bookingId;
    }

    /// <summary>An inbound bill no single booking fits (§6.5 step 4, §6.6).</summary>
    public void MarkUnmatched(string reason)
    {
        RequireInbound();
        Status = PosMessageStatus.Unmatched;
        UnmatchedReason = reason;
    }

    private void RequireOutbound(PosMessageStatus status)
    {
        if (Direction != PosDirection.Outbound || Status != status)
        {
            throw new InvalidOperationException($"POS message {Id.Value} is {Direction} {Status}, not Outbound {status}.");
        }
    }

    private void RequireInbound()
    {
        if (Direction != PosDirection.Inbound || Status != PosMessageStatus.Received)
        {
            throw new InvalidOperationException($"POS message {Id.Value} is {Direction} {Status}, not Inbound Received.");
        }
    }
}
