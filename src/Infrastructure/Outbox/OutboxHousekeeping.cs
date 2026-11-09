using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// Deletes dispatched outbox rows older than 30 days, once at start and then daily (Design §16, ADR-002).
/// The audit log keeps its own copy. Runs on the infrastructure clock.
/// </summary>
public sealed partial class OutboxHousekeeping(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<OutboxHousekeeping> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var cutoff = clock.GetUtcNow() - Retention;
        var deleted = await db.Outbox
            .Where(x => x.Status == OutboxStatus.Dispatched && x.DispatchedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox housekeeping deleted {Count} dispatched rows")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox housekeeping failed")]
    private static partial void LogPurgeFailed(ILogger logger, Exception error);
}
