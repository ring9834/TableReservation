using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Realtime;

/// <summary>
/// Design §16 <c>WaitlistOverdueJob</c> (AC4.6, BR-10): every 30 seconds on the infrastructure clock, finds notified
/// entries that have become overdue on the business clock (notified more than the venue's timeout ago) and pushes
/// <c>waitlistChanged {entryIds}</c> once per entry, so host screens show the flag without polling. Overdue is computed,
/// not stored (§3.2), so the job writes nothing; it remembers in memory which entries it has already pushed. After a
/// restart an entry may be pushed once more, which only costs a refetch. Runs only when <c>Outbox:RunWorkers</c> is on.
/// </summary>
internal sealed partial class WaitlistOverdueJob(
    IServiceScopeFactory scopeFactory,
    IVenueBroadcaster broadcaster,
    IOptions<OutboxOptions> options,
    TimeProvider businessClock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<WaitlistOverdueJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private HashSet<WaitlistEntryId> _pushed = [];

    /// <summary>One check: pushes newly overdue entries per venue and returns how many it pushed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var timeouts = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .ToDictionaryAsync(v => v.Id, v => v.Settings.NotifiedTimeoutMinutes, cancellationToken);
        var notified = await db.WaitlistEntries.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(w => w.Status == WaitlistStatus.Notified)
            .ToListAsync(cancellationToken);

        var now = businessClock.GetUtcNow();
        var overdue = notified
            .Where(e => timeouts.TryGetValue(e.VenueId, out var timeout) && e.IsOverdue(now, timeout))
            .ToList();
        var pushed = 0;
        var failed = new HashSet<WaitlistEntryId>();
        foreach (var venue in overdue.Where(e => !_pushed.Contains(e.Id)).GroupBy(e => e.VenueId))
        {
            var ids = venue.Select(e => e.Id.Value).ToList();
            var notice = new RealtimeNotice(NoticeMethods.WaitlistChanged, new WaitlistChangedNotice(ids, null, null));
            try
            {
                await broadcaster.SendAsync(venue.Key, notice, cancellationToken);
                pushed += ids.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogPushFailed(logger, ex.GetType().Name);
                failed.UnionWith(venue.Select(e => e.Id));
            }
        }

        // Entries that ended or were re-notified drop out, so a later overdue is pushed again; a failed push is retried.
        _pushed = [.. overdue.Select(e => e.Id).Where(id => !failed.Contains(id))];
        return pushed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunWorkers)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, infrastructureClock);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCheckFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waitlist overdue check: {ExceptionType} while pushing waitlistChanged; screens refetch on reconnect")]
    private static partial void LogPushFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Waitlist overdue check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception error);
}
