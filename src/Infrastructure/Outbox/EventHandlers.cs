using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Contracts;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Outbox;

/// <summary>The envelope fields a handler may need besides the event itself (Design §5.1).</summary>
public sealed record EventContext(
    Guid EventId,
    string EventType,
    int EventVersion,
    string AggregateType,
    Guid AggregateId,
    long Sequence,
    Guid CorrelationId,
    Guid? CausationId,
    EnvelopeActor Actor,
    JsonElement Payload)
{
    public static EventContext From(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new EventContext(
            envelope.EventId, envelope.EventType, envelope.EventVersion, envelope.AggregateType, envelope.AggregateId,
            envelope.Sequence, envelope.CorrelationId, envelope.CausationId, envelope.Actor, envelope.Payload);
    }
}

/// <summary>
/// A consumer of one event type (Design §5.1). Runs in the dispatcher's transaction and writes through the scoped
/// <see cref="ReservationsDbContext"/>; register it with <see cref="EventHandlerRegistration.AddEventHandler"/>.
/// </summary>
public interface IEventHandler<in T>
    where T : IDomainEvent
{
    /// <summary>Stable name; the inbox key together with the event ID.</summary>
    string ConsumerName { get; }

    Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Makes a handler run once per event (Design §5.4): its own savepoint in the dispatcher's transaction, an inbox
/// row <c>(consumer, eventId)</c> written in that savepoint, the handler, then its save. A repeat finds the inbox
/// row and does nothing; a failure rolls back to the savepoint (its work and its inbox row only) and rethrows.
/// <c>inbox.processed_at</c> comes from the infrastructure clock.
/// </summary>
public sealed class IdempotentHandlerDecorator<T>(
    IEventHandler<T> inner,
    ReservationsDbContext db,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock)
    : IEventHandler<T>
    where T : IDomainEvent
{
    /// <summary>One name is enough: handlers run one at a time and each releases or rolls back its savepoint.</summary>
    public const string Savepoint = "outbox_handler";

    public string ConsumerName => inner.ConsumerName;

    public async Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Event handlers run inside the dispatcher's transaction.");

        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
        try
        {
            var consumer = inner.ConsumerName;
            var messageId = domainEvent.EventId.ToString();
            var processedAt = infrastructureClock.GetUtcNow().ToUniversalTime();
            var inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO inbox (consumer, message_id, processed_at) VALUES ({consumer}, {messageId}, {processedAt}) ON CONFLICT DO NOTHING",
                cancellationToken);
            if (inserted > 0)
            {
                await inner.HandleAsync(domainEvent, context, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
        }
        catch
        {
            await transaction.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            throw;
        }
    }
}

public static class EventHandlerRegistration
{
    /// <summary>
    /// Registers <paramref name="handlerType"/> as a consumer of <paramref name="eventType"/>. With
    /// <paramref name="idempotent"/> (the default) it is wrapped in <see cref="IdempotentHandlerDecorator{T}"/>.
    /// Unwrapped handlers must be safe to repeat and must not write to the database (e.g. a SignalR push).
    /// </summary>
    public static IServiceCollection AddEventHandler(this IServiceCollection services, Type eventType, Type handlerType, bool idempotent = true)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlerType);

        var serviceType = typeof(IEventHandler<>).MakeGenericType(eventType);
        if (!serviceType.IsAssignableFrom(handlerType))
        {
            throw new ArgumentException($"{handlerType.Name} does not handle {eventType.Name}.", nameof(handlerType));
        }

        services.Add(ServiceDescriptor.Scoped(handlerType, handlerType));
        if (!idempotent)
        {
            services.AddScoped(serviceType, provider => provider.GetRequiredService(handlerType));
            return services;
        }

        var decoratorType = typeof(IdempotentHandlerDecorator<>).MakeGenericType(eventType);
        services.AddScoped(serviceType, provider => Activator.CreateInstance(
            decoratorType,
            provider.GetRequiredService(handlerType),
            provider.GetRequiredService<ReservationsDbContext>(),
            provider.GetRequiredKeyedService<TimeProvider>(Clocks.Infrastructure))!);
        return services;
    }

    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services, bool idempotent = true)
        where TEvent : IDomainEvent
        where THandler : class, IEventHandler<TEvent> =>
        services.AddEventHandler(typeof(TEvent), typeof(THandler), idempotent);
}
