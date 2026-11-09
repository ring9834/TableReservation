using System.Text.Json;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Audit;

/// <summary>
/// Writes one <c>audit_log</c> row per event (Design §4.3): who did what, and when. Registered for every event
/// type and wrapped in the idempotent decorator; <c>audit_log.event_id</c> is unique as a second guard. The summary
/// is the event payload, which carries IDs, never personal details (ADR-002).
/// </summary>
public sealed class AuditHandler<T>(ReservationsDbContext db) : IEventHandler<T>
    where T : IDomainEvent
{
    public const string Consumer = "audit";

    public string ConsumerName => Consumer;

    public Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        db.Audit.Add(new AuditEntry(
            domainEvent.VenueId,
            domainEvent.EventId,
            context.EventType,
            context.AggregateId,
            JsonSerializer.SerializeToDocument(context.Actor, EventJson.Options),
            domainEvent.OccurredAt,
            JsonSerializer.SerializeToDocument(context.Payload, EventJson.Options)));
        return Task.CompletedTask;
    }
}
