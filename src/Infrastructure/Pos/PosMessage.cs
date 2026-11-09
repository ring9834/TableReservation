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
/// One row of <c>pos_messages</c>, for both directions (Design §6.1, §13). A delivery queue item, not an
/// aggregate: it raises no domain events. Behaviour arrives with the POS milestone (M9).
/// </summary>
public sealed class PosMessage
{
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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>Inbound: the booking the bill was matched to (§6.5).</summary>
    public BookingId? BookingId { get; private set; }

    /// <summary>Inbound: <c>no_match</c>, <c>ambiguous</c> or <c>second_bill</c> (§6.5, §6.6).</summary>
    public string? UnmatchedReason { get; private set; }
}
