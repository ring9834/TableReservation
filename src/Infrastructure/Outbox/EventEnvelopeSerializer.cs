using System.Text.Json;
using System.Text.Json.Nodes;
using Reservation.Contracts;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// Converts between domain events and outbox rows (Design §5.1). The payload is the event's own fields; the
/// envelope carries <c>eventId</c>, <c>occurredAt</c> and <c>venueId</c>, so the payload leaves them out.
/// </summary>
public sealed class EventEnvelopeSerializer(EventTypeRegistry registry)
{
    private static readonly string[] _envelopeFields = ["eventId", "occurredAt", "venueId"];

    /// <summary>Wraps a raised event in the envelope. <paramref name="createdAt"/> is infrastructure time.</summary>
    public OutboxMessage Serialize(RaisedEvent raised, Guid aggregateId, IRequestContext context, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(raised);
        ArgumentNullException.ThrowIfNull(context);

        var domainEvent = raised.Event;
        var metadata = registry.MetadataOf(domainEvent.GetType());
        var payload = JsonSerializer.SerializeToNode(domainEvent, domainEvent.GetType(), EventJson.Options)!.AsObject();
        foreach (var field in _envelopeFields)
        {
            payload.Remove(field);
        }

        var envelope = new EventEnvelope(
            domainEvent.EventId,
            metadata.Name,
            metadata.Version,
            domainEvent.OccurredAt.ToUniversalTime(),
            domainEvent.VenueId.Value,
            metadata.AggregateType,
            aggregateId,
            raised.Sequence,
            context.CorrelationId,
            context.CausationId,
            ToEnvelopeActor(context.Actor),
            JsonSerializer.SerializeToElement(payload, EventJson.Options));

        return new OutboxMessage(
            envelope.EventId, domainEvent.VenueId, envelope.EventType, envelope.EventVersion, envelope.AggregateType,
            aggregateId, raised.Sequence, envelope.CorrelationId, envelope.CausationId,
            JsonSerializer.SerializeToDocument(envelope, EventJson.Options), createdAt);
    }

    public static EventEnvelope ReadEnvelope(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Envelope.Deserialize<EventEnvelope>(EventJson.Options)
            ?? throw new JsonException($"Outbox row {message.EventId} has an empty envelope.");
    }

    /// <summary>Reads the event back, upcasting an older version to the current one.</summary>
    public IDomainEvent Deserialize(OutboxMessage message)
    {
        var envelope = ReadEnvelope(message);
        var current = registry.Current(envelope.EventType);
        var payload = JsonNode.Parse(envelope.Payload.GetRawText())!.AsObject();
        if (envelope.EventVersion != current.Version)
        {
            payload = Upcast(envelope.EventType, payload, envelope.EventVersion, current.Version);
        }

        payload["eventId"] = envelope.EventId;
        payload["occurredAt"] = envelope.OccurredAt;
        payload["venueId"] = envelope.VenueId;
        return (IDomainEvent)(payload.Deserialize(registry.Resolve(envelope.EventType, current.Version), EventJson.Options)
            ?? throw new JsonException($"Outbox row {message.EventId} has an empty payload."));
    }

    public static EnvelopeActor ToEnvelopeActor(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new EnvelopeActor(JsonNamingPolicy.CamelCase.ConvertName(actor.Type.ToString()), actor.Id, actor.Role);
    }

    /// <summary>
    /// ADR-002 versioning: old rows are upgraded step by step when read. Every event is still v1, so there are
    /// no upcasters yet; the first v2 adds a case here.
    /// </summary>
    private static JsonObject Upcast(string eventType, JsonObject payload, int fromVersion, int toVersion) =>
        fromVersion < toVersion
            ? throw new NotSupportedException($"No upcaster from {eventType} v{fromVersion} to v{toVersion} ({payload.Count} fields).")
            : throw new NotSupportedException($"{eventType} v{fromVersion} is newer than this build (v{toVersion}).");
}
