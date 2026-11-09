namespace Reservation.Domain.Common;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }

    VenueId VenueId { get; }
}

/// <summary>Base record for all domain events (Design §4.1).</summary>
public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId) : IDomainEvent
{
    /// <summary>A new UUID v7 event ID, ordered by the time the event happened.</summary>
    public static Guid NewEventId(DateTimeOffset now) => Guid.CreateVersion7(now);
}

/// <summary>Name, version and aggregate type written to the outbox envelope (Design §4.1).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventMetadataAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public int Version { get; set; } = 1;

    public string AggregateType { get; set; } = string.Empty;
}
