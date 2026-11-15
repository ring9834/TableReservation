using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// Sends outbound POS messages per venue, strictly in <c>venue_sequence</c> order (Design §6.2, §6.4, §6.7, AC6.5,
/// QA-5). Wakes on <see cref="PosSignal"/> or every <see cref="PosOptions.PollInterval"/>.
/// <list type="bullet">
/// <item>One sender per venue, even across API instances: each send runs in a transaction that first takes
/// <c>pg_try_advisory_xact_lock</c> on the venue; a worker that can't get it leaves the venue to the one that has it.</item>
/// <item>The venue's head is its lowest outbound row that is <c>Waiting</c> or <c>Failed</c>. Message n + 1 is never
/// tried while n is not <c>Sent</c>. A <c>Failed</c> head blocks the venue's queue until an admin replays it (pilot).</item>
/// <item>One message per transaction: accepted → <c>Sent</c> and committed (the POS log sees each one as it goes);
/// unavailable → retried after <see cref="PosOptions.UnavailableRetry"/> without counting an attempt, failing only after
/// <see cref="PosOptions.UnavailableWindow"/>; a temporary error or an adapter exception → one attempt and
/// <see cref="OutboxRetryPolicy"/>'s backoff; a rejection → <c>Failed</c> at once.</item>
/// <item>Every send carries the message's idempotency key, so a crash after the POS accepted and before <c>Sent</c> was
/// saved is resent and ignored by the POS as a repeat.</item>
/// </list>
/// Two clocks (§5.6): the loop, "due" and retries use the infrastructure clock; <c>delivered_at</c>, which people see,
/// uses the business clock. Logs carry IDs, types, sequence, status and codes only, never payloads.
/// </summary>
public sealed partial class PosDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    PosSignal signal,
    IOptions<PosOptions> options,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider businessClock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<PosDeliveryWorker> logger)
    : BackgroundService
{
    /// <summary>The last error when the adapter threw instead of answering: the exception type only.</summary>
    public const string AdapterErrorPrefix = "adapter_exception:";

    /// <summary>The failure reason when temporary errors used up the retry window.</summary>
    public const string RetriesUsedUp = "retries_used_up";

    private readonly PosOptions _options = options.Value;

    /// <summary>One pass over every venue with a due message. Returns how many messages were tried.</summary>
    public async Task<int> DeliverOnceAsync(CancellationToken cancellationToken)
    {
        List<Guid> venues;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var now = clock.GetUtcNow().ToUniversalTime();
            venues = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT venue_id AS "Value" FROM pos_messages
                WHERE direction = 'Outbound' AND status = 'Waiting' AND next_attempt_at <= {now}
                """).ToListAsync(cancellationToken);
        }

        var tried = 0;
        foreach (var venueId in venues)
        {
            tried += await DeliverVenueAsync(new VenueId(venueId), cancellationToken);
        }

        return tried;
    }

    /// <summary>
    /// The POS is reachable again (the mock was switched online): make every waiting message due now and wake the
    /// worker, so the queue drains at once instead of at the next 5-second retry. Attempts are unchanged.
    /// </summary>
    public async Task RetryNowAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var now = clock.GetUtcNow().ToUniversalTime();
            await db.PosMessages.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .Where(m => m.Direction == PosDirection.Outbound && m.Status == PosMessageStatus.Waiting && m.NextAttemptAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, now), cancellationToken);
        }

        signal.Notify();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!outboxOptions.Value.RunWorkers)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPassFailed(logger, ex.GetType().Name);
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

    /// <summary>Sends the venue's due messages in order until one isn't accepted. Returns how many were tried.</summary>
    private async Task<int> DeliverVenueAsync(VenueId venueId, CancellationToken cancellationToken)
    {
        var tried = 0;
        while (true)
        {
            var outcome = await SendHeadAsync(venueId, cancellationToken);
            if (outcome is null)
            {
                return tried;
            }

            tried++;
            if (outcome != PosSendOutcome.Accepted)
            {
                return tried;
            }
        }
    }

    /// <summary>
    /// One transaction: the venue lock, the head row, one send, its outcome. <c>null</c> when nothing was sent: another
    /// worker holds the venue, the queue is empty or blocked, or the head isn't due.
    /// </summary>
    private async Task<PosSendOutcome?> SendHeadAsync(VenueId venueId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ReservationsDbContext>();
        var adapter = services.GetRequiredService<IPosAdapter>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = $"pos-delivery:{venueId.Value}";
        var locked = await db.Database.SqlQuery<bool>(
            $"""SELECT pg_try_advisory_xact_lock(hashtextextended({lockKey}, 0)) AS "Value" """).ToListAsync(cancellationToken);
        if (!locked.Single())
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var venue = venueId.Value;
        var head = await db.PosMessages
            .FromSql($"""
                SELECT p.* FROM pos_messages p
                WHERE p.venue_id = {venue} AND p.direction = 'Outbound' AND p.status IN ('Waiting', 'Failed')
                ORDER BY p.venue_sequence
                LIMIT 1
                FOR UPDATE
                """)
            .IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .SingleOrDefaultAsync(cancellationToken);

        var now = clock.GetUtcNow().ToUniversalTime();
        if (head is null || head.Status == PosMessageStatus.Failed || head.NextAttemptAt > now)
        {
            if (head is { Status: PosMessageStatus.Failed })
            {
                LogBlocked(logger, venue, head.Id.Value, head.VenueSequence);
            }

            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var result = await SendAsync(adapter, head, cancellationToken);
        switch (result.Outcome)
        {
            case PosSendOutcome.Accepted:
                head.MarkSent(businessClock.GetUtcNow());
                break;
            case PosSendOutcome.Unavailable:
                head.DeferUnavailable(now, now + _options.UnavailableRetry, _options.UnavailableWindow);
                break;
            case PosSendOutcome.Rejected:
                head.Fail(result.Error ?? "rejected");
                break;
            default:
                var retry = OutboxRetryPolicy.Next(head.Attempts + 1, head.QueuedAt, now);
                if (retry.Status == OutboxStatus.Failed)
                {
                    head.ScheduleRetry(result.Error ?? "temporary_error", now);
                    head.Fail(RetriesUsedUp);
                }
                else
                {
                    head.ScheduleRetry(result.Error ?? "temporary_error", retry.NextAttemptAt);
                }

                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Log(head, result);
        return result.Outcome;
    }

    /// <summary>An adapter that throws counts as a temporary error; only the exception type is kept.</summary>
    private static async Task<PosSendResult> SendAsync(IPosAdapter adapter, PosMessage message, CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.SendAsync(
                new PosOutbound(message.Id, message.VenueId, message.MessageType, message.IdempotencyKey, message.BookingId, message.Payload),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PosSendResult.Temporary(AdapterErrorPrefix + ex.GetType().Name);
        }
    }

    private void Log(PosMessage message, PosSendResult result)
    {
        switch (message.Status)
        {
            case PosMessageStatus.Sent:
                LogSent(logger, message.Id.Value, message.MessageType, message.VenueSequence, message.Attempts, result.Duplicate);
                break;
            case PosMessageStatus.Failed:
                LogFailed(logger, message.Id.Value, message.MessageType, message.VenueSequence, message.Attempts, message.LastError);
                break;
            default:
                LogRetry(logger, message.Id.Value, message.MessageType, message.VenueSequence, message.Attempts, message.LastError, message.NextAttemptAt);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "POS: {PosMessageId} ({MessageType}, #{VenueSequence}) sent after {Attempts} attempts; repeat at the POS: {Duplicate}")]
    private static partial void LogSent(ILogger logger, Guid posMessageId, string messageType, long venueSequence, int attempts, bool duplicate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "POS: {PosMessageId} ({MessageType}, #{VenueSequence}) not sent after {Attempts} attempts ({Error}); retry at {NextAttemptAt}")]
    private static partial void LogRetry(ILogger logger, Guid posMessageId, string messageType, long venueSequence, int attempts, string? error, DateTimeOffset? nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "POS: {PosMessageId} ({MessageType}, #{VenueSequence}) failed after {Attempts} attempts ({Error}); the venue's queue waits behind it")]
    private static partial void LogFailed(ILogger logger, Guid posMessageId, string messageType, long venueSequence, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "POS: venue {VenueId} is blocked by failed message {PosMessageId} (#{VenueSequence})")]
    private static partial void LogBlocked(ILogger logger, Guid venueId, Guid posMessageId, long venueSequence);

    [LoggerMessage(Level = LogLevel.Error, Message = "POS: delivery pass failed with {ExceptionType}")]
    private static partial void LogPassFailed(ILogger logger, string exceptionType);
}
