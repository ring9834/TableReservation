namespace Reservation.Domain.Common;

/// <summary>What the outbox needs from any aggregate, without knowing its type (Design §2.2, §5.2).</summary>
public interface IAggregateRoot
{
    Guid AggregateId { get; }

    long LastEventSequence { get; }

    IReadOnlyList<RaisedEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>An event together with the sequence number its aggregate stamped on it.</summary>
public sealed record RaisedEvent(IDomainEvent Event, long Sequence);

/// <summary>
/// Base class for every aggregate root (Design §2.2). Events are raised only from inside the
/// aggregate's own methods; each one increases <see cref="LastEventSequence"/> by exactly 1.
/// <see cref="Version"/> is the row version (PostgreSQL xmin) and is for concurrency only.
/// </summary>
public abstract class AggregateRoot<TId> : IAggregateRoot
    where TId : struct, IEntityId
{
    private readonly List<RaisedEvent> _domainEvents = [];

    public TId Id { get; protected init; }

    public uint Version { get; private set; }

    public long LastEventSequence { get; private set; }

    public IReadOnlyList<RaisedEvent> DomainEvents => _domainEvents;

    Guid IAggregateRoot.AggregateId => Id.Value;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        LastEventSequence++;
        _domainEvents.Add(new RaisedEvent(domainEvent, LastEventSequence));
    }
}
