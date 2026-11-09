using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// Delivers outbox rows to their handlers (Design §5.3). Wakes on <see cref="OutboxSignal"/> or every
/// <see cref="OutboxOptions.PollInterval"/>; each batch is one transaction that claims rows with
/// <c>FOR UPDATE SKIP LOCKED</c>. Only the lowest undelivered sequence of each aggregate is eligible, so a failing
/// event holds back later events of its own aggregate only. Every handler runs, each in its own savepoint; the row
/// is <see cref="OutboxStatus.Dispatched"/> only when all of them succeeded, otherwise it is retried by
/// <see cref="OutboxRetryPolicy"/>. All timing uses the infrastructure clock.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
    : BackgroundService
{
    private const int MaxErrorLength = 2000;

    private static readonly ConcurrentDictionary<Type, IHandlerInvoker> _invokers = new();

    private readonly OutboxOptions _options = options.Value;

    /// <summary>One claim–deliver–commit cycle. Returns the number of rows it handled.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ReservationsDbContext>();
        var serializer = services.GetRequiredService<EventEnvelopeSerializer>();
        var requestContext = services.GetRequiredService<RequestContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        var batchSize = _options.BatchSize;
        var rows = await db.Outbox
            .FromSql($"""
                SELECT o.* FROM outbox o
                WHERE o.status = 'Pending' AND o.next_attempt_at <= {now}
                  AND NOT EXISTS (
                    SELECT 1 FROM outbox e
                    WHERE e.aggregate_id = o.aggregate_id AND e.sequence < o.sequence AND e.status <> 'Dispatched')
                ORDER BY o.next_attempt_at, o.event_id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var failures = await DeliverAsync(services, db, serializer, requestContext, row, cancellationToken);
            await RecordOutcomeAsync(db, row, failures, cancellationToken);
            db.ChangeTracker.Clear();
        }

        await transaction.CommitAsync(cancellationToken);
        return rows.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunWorkers)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int handled;
            try
            {
                handled = await DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogBatchFailed(logger, ex);
                handled = 0;
            }

            // A full batch means more may be waiting: go again straight away.
            if (handled >= _options.BatchSize)
            {
                continue;
            }

            try
            {
                await signal.WaitAsync(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static async Task<List<(string Consumer, Exception Error)>> DeliverAsync(
        IServiceProvider services, ReservationsDbContext db, EventEnvelopeSerializer serializer, RequestContext requestContext,
        OutboxMessage row, CancellationToken cancellationToken)
    {
        IDomainEvent domainEvent;
        EventContext context;
        try
        {
            domainEvent = serializer.Deserialize(row);
            context = EventContext.From(EventEnvelopeSerializer.ReadEnvelope(row));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [("dispatcher", ex)];
        }

        // Events raised by handlers belong to the same story and were caused by this event.
        requestContext.Set(row.CorrelationId, row.EventId, Actor.ForSystem());

        var invoker = _invokers.GetOrAdd(
            domainEvent.GetType(),
            type => (IHandlerInvoker)Activator.CreateInstance(typeof(HandlerInvoker<>).MakeGenericType(type))!);
        return await invoker.InvokeAsync(services, db, domainEvent, context, cancellationToken);
    }

    private async Task RecordOutcomeAsync(
        ReservationsDbContext db, OutboxMessage row, List<(string Consumer, Exception Error)> failures, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var outbox = db.Outbox.Where(x => x.EventId == row.EventId);
        if (failures.Count == 0)
        {
            await outbox.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Status, OutboxStatus.Dispatched)
                    .SetProperty(x => x.DispatchedAt, (DateTimeOffset?)now)
                    .SetProperty(x => x.LastError, (string?)null),
                cancellationToken);
            return;
        }

        var attempts = row.Attempts + 1;
        var retry = OutboxRetryPolicy.Next(attempts, row.CreatedAt, now);
        var lastError = string.Join(
            "; ", failures.Select(f => $"{f.Consumer}: {f.Error.GetType().Name}: {f.Error.Message}"));
        if (lastError.Length > MaxErrorLength)
        {
            lastError = lastError[..MaxErrorLength];
        }

        await outbox.ExecuteUpdateAsync(
            s => s.SetProperty(x => x.Status, retry.Status)
                .SetProperty(x => x.Attempts, attempts)
                .SetProperty(x => x.NextAttemptAt, retry.NextAttemptAt)
                .SetProperty(x => x.LastError, lastError),
            cancellationToken);

        foreach (var (consumer, error) in failures)
        {
            if (retry.Status == OutboxStatus.Failed)
            {
                LogFailed(logger, error, row.EventType, row.EventId, row.AggregateId, consumer, attempts);
            }
            else
            {
                LogRetry(logger, error, row.EventType, row.EventId, consumer, attempts, retry.NextAttemptAt);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox: {EventType} {EventId} failed in {Consumer} (attempt {Attempts}); retry at {NextAttemptAt}")]
    private static partial void LogRetry(ILogger logger, Exception error, string eventType, Guid eventId, string consumer, int attempts, DateTimeOffset nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox: {EventType} {EventId} marked failed after {Attempts} attempts ({Consumer}); later events for {AggregateId} are held")]
    private static partial void LogFailed(ILogger logger, Exception error, string eventType, Guid eventId, Guid aggregateId, string consumer, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox: dispatch batch failed")]
    private static partial void LogBatchFailed(ILogger logger, Exception error);

    private interface IHandlerInvoker
    {
        Task<List<(string Consumer, Exception Error)>> InvokeAsync(
            IServiceProvider services, ReservationsDbContext db, IDomainEvent domainEvent, EventContext context, CancellationToken cancellationToken);
    }

    private sealed class HandlerInvoker<T> : IHandlerInvoker
        where T : IDomainEvent
    {
        public async Task<List<(string Consumer, Exception Error)>> InvokeAsync(
            IServiceProvider services, ReservationsDbContext db, IDomainEvent domainEvent, EventContext context, CancellationToken cancellationToken)
        {
            var failures = new List<(string Consumer, Exception Error)>();
            foreach (var handler in services.GetServices<IEventHandler<T>>())
            {
                try
                {
                    await handler.HandleAsync((T)domainEvent, context, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures.Add((handler.ConsumerName, ex));

                    // The savepoint undid the handler's rows; drop what it left tracked so nothing stale leaks on.
                    db.ChangeTracker.Clear();
                }
            }

            return failures;
        }
    }
}
