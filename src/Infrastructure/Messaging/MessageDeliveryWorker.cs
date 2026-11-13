using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// Sends queued messages and records delivery (Design §7.1, §7.2). Wakes on <see cref="MessageSignal"/> or every
/// <see cref="MessagingOptions.PollInterval"/>; each batch is one transaction that claims due rows with
/// <c>FOR UPDATE SKIP LOCKED</c>, so two workers never send one message twice.
/// <list type="bullet">
/// <item>Queued and expired: Failed (<c>expired</c>).</item>
/// <item>Queued: sent through <see cref="IMessageSender"/> with the message ID as the client reference. Accepted → Sent
/// (raises <c>MessageSent</c> through the outbox); temporary error → retried with backoff; permanent error → Failed.</item>
/// <item>Sent with a delivery due (the demo phone panel): Delivered.</item>
/// </list>
/// Two clocks (§5.6): the loop, the claim's "now", retries, expiry and the delivery due time use the infrastructure clock,
/// so texts still go out while the demo clock stands still; <c>sent_at</c> and <c>delivered_at</c>, which people see, use
/// the business clock. Logs carry the message ID, purpose, status and attempts only.
/// </summary>
public sealed partial class MessageDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    MessageSignal signal,
    IOptions<MessagingOptions> options,
    IOptions<OutboxOptions> outboxOptions,
    TimeProvider businessClock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<MessageDeliveryWorker> logger)
    : BackgroundService
{
    public const string Expired = "expired";

    /// <summary>The last error when the sender threw instead of answering: the exception type only.</summary>
    public const string SenderErrorPrefix = "sender_exception:";

    private readonly MessagingOptions _options = options.Value;

    /// <summary>One claim–send–commit cycle. Returns the number of rows it handled.</summary>
    public async Task<int> DeliverBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ReservationsDbContext>();
        var sender = services.GetRequiredService<IMessageSender>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow().ToUniversalTime();
        var batchSize = _options.BatchSize;
        var rows = await db.Messages
            .FromSql($"""
                SELECT m.*, m.xmin FROM messages m
                WHERE (m.status = 'Queued' AND m.next_attempt_at <= {now})
                   OR (m.status = 'Sent' AND m.delivery_due_at <= {now})
                ORDER BY coalesce(m.delivery_due_at, m.next_attempt_at), m.id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var guestIds = rows.Where(m => m.Status == MessageStatus.Queued).Select(m => m.GuestId).Distinct().ToList();
        var mobiles = await db.Guests
            .IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .AsNoTracking()
            .Where(g => guestIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Mobile })
            .ToDictionaryAsync(g => g.Id, g => g.Mobile, cancellationToken);

        foreach (var message in rows)
        {
            if (message.Status == MessageStatus.Sent)
            {
                Check(message.MarkDelivered(businessClock.GetUtcNow()));
            }
            else if (message.IsExpired(now))
            {
                Check(message.MarkFailed(Expired));
            }
            else if (!mobiles.TryGetValue(message.GuestId, out var mobile))
            {
                Check(message.MarkFailed("no_recipient"));
            }
            else
            {
                var result = await SendAsync(sender, message, mobile.E164, cancellationToken);
                switch (result.Outcome)
                {
                    case SendOutcome.Accepted:
                        var due = result.DeliveredAfter is { } after ? now + after : (DateTimeOffset?)null;
                        Check(message.MarkSent(result.ProviderRef!, mobile.Masked(), businessClock.GetUtcNow(), due));
                        break;
                    case SendOutcome.Permanent:
                        Check(message.MarkFailed(result.Error ?? "permanent_error"));
                        break;
                    default:
                        Check(message.ScheduleRetry(result.Error ?? "temporary_error", now));
                        break;
                }
            }

            Log(message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!outboxOptions.Value.RunWorkers)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int handled;
            try
            {
                handled = await DeliverBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogBatchFailed(logger, ex.GetType().Name);
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

    /// <summary>
    /// A sender that throws is treated as a temporary error. Only the exception type is kept: a provider's message could
    /// echo the number or the body.
    /// </summary>
    private static async Task<SendResult> SendAsync(IMessageSender sender, Message message, string to, CancellationToken cancellationToken)
    {
        try
        {
            return await sender.SendAsync(new OutgoingMessage(message.Id, message.Channel, to, message.Body), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Temporary(SenderErrorPrefix + ex.GetType().Name);
        }
    }

    private static void Check(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Message transition refused: {result.Error!.Code}.");
        }
    }

    private void Log(Message message)
    {
        if (message.Status == MessageStatus.Failed)
        {
            LogFailed(logger, message.Id.Value, message.Purpose, message.Attempts, message.LastError);
        }
        else if (message.Status == MessageStatus.Queued)
        {
            LogRetry(logger, message.Id.Value, message.Purpose, message.Attempts, message.LastError, message.NextAttemptAt);
        }
        else
        {
            LogStatus(logger, message.Id.Value, message.Purpose, message.Status, message.Attempts);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Messages: {MessageId} ({Purpose}) is {Status} after {Attempts} attempts")]
    private static partial void LogStatus(ILogger logger, Guid messageId, MessagePurpose purpose, MessageStatus status, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messages: {MessageId} ({Purpose}) attempt {Attempts} failed ({Error}); retry at {NextAttemptAt}")]
    private static partial void LogRetry(ILogger logger, Guid messageId, MessagePurpose purpose, int attempts, string? error, DateTimeOffset nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messages: {MessageId} ({Purpose}) failed after {Attempts} attempts ({Error})")]
    private static partial void LogFailed(ILogger logger, Guid messageId, MessagePurpose purpose, int attempts, string? error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Messages: delivery batch failed with {ExceptionType}")]
    private static partial void LogBatchFailed(ILogger logger, string exceptionType);
}
