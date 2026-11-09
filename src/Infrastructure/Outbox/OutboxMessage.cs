using System.Text.Json;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

public enum OutboxStatus
{
    Pending,
    Dispatched,
    Failed,
}

/// <summary>
/// One row of the <c>outbox</c> table (Design §5.1, §13). The event fields are written once with the
/// change that raised the event; the delivery fields are updated by the dispatcher (M3).
/// </summary>
public sealed class OutboxMessage
{
    public OutboxMessage(
        Guid eventId, VenueId venueId, string eventType, int eventVersion, string aggregateType, Guid aggregateId,
        long sequence, Guid correlationId, Guid? causationId, JsonDocument envelope, DateTimeOffset createdAt)
    {
        EventId = eventId;
        VenueId = venueId;
        EventType = eventType;
        EventVersion = eventVersion;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        Sequence = sequence;
        CorrelationId = correlationId;
        CausationId = causationId;
        Envelope = envelope;
        CreatedAt = createdAt;
        NextAttemptAt = createdAt;
        Status = OutboxStatus.Pending;
    }

    public Guid EventId { get; private set; }

    public VenueId VenueId { get; private set; }

    public string EventType { get; private set; }

    public int EventVersion { get; private set; }

    public string AggregateType { get; private set; }

    public Guid AggregateId { get; private set; }

    public long Sequence { get; private set; }

    public Guid CorrelationId { get; private set; }

    public Guid? CausationId { get; private set; }

    public JsonDocument Envelope { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }
}
