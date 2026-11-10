using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Idempotency;

/// <summary>
/// Deletes idempotency keys older than 24 hours, at start and then hourly (ADR-004). Runs on the infrastructure clock,
/// and only when <c>Outbox:RunWorkers</c> is on (the API test host turns the workers off).
/// </summary>
public sealed partial class IdempotencyHousekeeping(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<IdempotencyHousekeeping> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var cutoff = clock.GetUtcNow() - IdempotencyStore.Retention;
        var deleted = await db.IdempotencyKeys.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        LogPurged(logger, deleted);
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunWorkers)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                try
                {
                    await PurgeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPurgeFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotency housekeeping deleted {Count} expired keys")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Idempotency housekeeping failed")]
    private static partial void LogPurgeFailed(ILogger logger, Exception error);
}
