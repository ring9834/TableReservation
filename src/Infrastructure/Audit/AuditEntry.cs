using System.Text.Json;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Audit;

/// <summary>One row of <c>audit_log</c> (ADR-003 DM-09, Design §13), written by the audit handler (M3).</summary>
public sealed class AuditEntry
{
    public AuditEntry(
        VenueId venueId, Guid eventId, string eventType, Guid aggregateId, JsonDocument actor, DateTimeOffset occurredAt, JsonDocument summary)
    {
        VenueId = venueId;
        EventId = eventId;
        EventType = eventType;
        AggregateId = aggregateId;
        Actor = actor;
        OccurredAt = occurredAt;
        Summary = summary;
    }

    public long Id { get; private set; }

    public VenueId VenueId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; }

    public Guid AggregateId { get; private set; }

    public JsonDocument Actor { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public JsonDocument Summary { get; private set; }
}
