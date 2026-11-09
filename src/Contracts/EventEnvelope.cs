using System.Text.Json;

namespace Reservation.Contracts;

/// <summary>
/// The standard wrapper around every domain event (ADR-002 "Event envelope", Architecture §6.5). Stored in
/// <c>outbox.envelope</c>; the worker, the audit log and any future broker read it without knowing the event type.
/// </summary>
/// <param name="EventId">UUID v7; the idempotency key.</param>
/// <param name="EventType">Catalogue name, e.g. <c>BookingSeated</c>.</param>
/// <param name="EventVersion">Starts at 1; raised only for a breaking change.</param>
/// <param name="OccurredAt">When the change happened (UTC, business time).</param>
/// <param name="VenueId">The venue the event belongs to.</param>
/// <param name="AggregateType">The record that changed, e.g. <c>Booking</c>.</param>
/// <param name="AggregateId">Its ID; sets the delivery order.</param>
/// <param name="Sequence">Increases by 1 per aggregate (the aggregate's LastEventSequence).</param>
/// <param name="CorrelationId">Shared by everything one guest or staff action caused.</param>
/// <param name="CausationId">The event or command that caused this one.</param>
/// <param name="Actor">Who did it; IDs only, never names or phone numbers.</param>
/// <param name="Payload">The event's own fields (money in cents, times in UTC, IDs not personal details).</param>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    Guid VenueId,
    string AggregateType,
    Guid AggregateId,
    long Sequence,
    Guid CorrelationId,
    Guid? CausationId,
    EnvelopeActor Actor,
    JsonElement Payload);

/// <summary>The envelope's <c>actor</c>: <c>{ type: guest | staff | system | pos, id, role }</c>.</summary>
public sealed record EnvelopeActor(string Type, string? Id = null, string? Role = null);
