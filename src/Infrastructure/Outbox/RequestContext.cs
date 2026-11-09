using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

/// <summary>Who and why (Design §5.1): copied into every event envelope written in this scope.</summary>
public interface IRequestContext
{
    /// <summary>Shared by everything one guest or staff action causes.</summary>
    Guid CorrelationId { get; }

    /// <summary>The event or command that caused the events written now.</summary>
    Guid? CausationId { get; }

    Actor Actor { get; }
}

/// <summary>
/// Scoped request context. M3: a new correlation ID per scope and the system actor; the dispatcher sets it per event
/// (correlation copied, causation = the event being handled). M4 fills it from the HTTP request and trace context.
/// </summary>
public sealed class RequestContext : IRequestContext
{
    /// <summary>The fixed demo host (Design §17); the same ID the seeder uses.</summary>
    public static readonly Actor DemoHost = Actor.ForStaff("demo-host", "Host");

    public Guid CorrelationId { get; private set; } = Guid.CreateVersion7();

    public Guid? CausationId { get; private set; }

    public Actor Actor { get; private set; } = Actor.ForSystem();

    public void Set(Guid correlationId, Guid? causationId, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CorrelationId = correlationId;
        CausationId = causationId;
        Actor = actor;
    }
}
