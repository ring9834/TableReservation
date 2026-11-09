using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// Writes every raised domain event to the outbox in the same save, so in the same transaction (Design §5.2).
/// Scoped: one instance per <c>DbContext</c>, because it remembers what the current save added.
/// <list type="bullet">
/// <item>Saving: collects <see cref="IAggregateRoot.DomainEvents"/> from every tracked aggregate and adds one
/// <see cref="OutboxMessage"/> per event, with the sequence stamped by <c>Raise</c>.</item>
/// <item>Saved: clears those aggregates' events and rings <see cref="OutboxSignal"/> once the data is committed
/// (straight away, or when the surrounding transaction commits).</item>
/// <item>Failed: detaches the rows it added, so a retried save doesn't add them twice.</item>
/// </list>
/// <c>outbox.created_at</c> comes from the infrastructure clock. Logs IDs only, never the payload.
/// </summary>
public sealed partial class OutboxSaveChangesInterceptor(
    EventEnvelopeSerializer serializer,
    IRequestContext requestContext,
    OutboxSignal signal,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<OutboxSaveChangesInterceptor> logger)
    : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly List<IAggregateRoot> _aggregates = [];
    private readonly List<OutboxMessage> _added = [];
    private bool _notifyOnCommit;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Discard(eventData.Context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Discard(eventData.Context);
    }

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed();

    Task IDbTransactionInterceptor.TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        Committed();
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => _notifyOnCommit = false;

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        _notifyOnCommit = false;
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        _aggregates.Clear();
        _added.Clear();
        var createdAt = clock.GetUtcNow();
        foreach (var aggregate in context.ChangeTracker.Entries<IAggregateRoot>()
                     .Where(e => e.State != EntityState.Detached)
                     .Select(e => e.Entity)
                     .Where(a => a.DomainEvents.Count > 0)
                     .ToList())
        {
            _aggregates.Add(aggregate);
            foreach (var raised in aggregate.DomainEvents)
            {
                var message = serializer.Serialize(raised, aggregate.AggregateId, requestContext, createdAt);
                context.Add(message);
                _added.Add(message);
                LogEventQueued(logger, message.EventType, message.EventId, message.AggregateType, message.AggregateId, message.Sequence);
            }
        }
    }

    private void Saved(DbContext? context)
    {
        foreach (var aggregate in _aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        if (_added.Count > 0)
        {
            LogEventsSaved(logger, _added.Count);
            if (context?.Database.CurrentTransaction is null)
            {
                signal.Notify();
            }
            else
            {
                _notifyOnCommit = true;
            }
        }

        _aggregates.Clear();
        _added.Clear();
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
        {
            foreach (var message in _added)
            {
                context.Entry(message).State = EntityState.Detached;
            }
        }

        _aggregates.Clear();
        _added.Clear();
    }

    private void Committed()
    {
        if (_notifyOnCommit)
        {
            _notifyOnCommit = false;
            signal.Notify();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox: {EventType} {EventId} for {AggregateType} {AggregateId}, sequence {Sequence}")]
    private static partial void LogEventQueued(ILogger logger, string eventType, Guid eventId, string aggregateType, Guid aggregateId, long sequence);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox: {Count} events saved with the change")]
    private static partial void LogEventsSaved(ILogger logger, int count);
}
