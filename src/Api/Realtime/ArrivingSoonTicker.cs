using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Realtime;

/// <summary>
/// Design §16 <c>ArrivingSoonTicker</c> (AC2.3; built in M11a, Design §17.2): every 60 seconds on the infrastructure
/// clock, and at once when the demo clock moves (<see cref="DemoClockSignal"/>), works out which tables are "arriving
/// soon" on the business clock (<see cref="TableStateRule"/>, the floor plan's own rule) and pushes
/// <c>floorPlanChanged {tableIds}</c> for the tables that turned arriving soon or stopped being so since the last pass,
/// so host screens refetch without polling. A table seated or freed meanwhile already had its own notice; a repeat
/// only costs a refetch. The first pass per venue only records. Writes nothing; remembers the last pass in memory.
/// Runs only when <c>Outbox:RunWorkers</c> is on.
/// </summary>
internal sealed partial class ArrivingSoonTicker(
    IServiceScopeFactory scopeFactory,
    IVenueBroadcaster broadcaster,
    IOptions<OutboxOptions> options,
    TimeProvider businessClock,
    DemoClockSignal clockSignal,
    ILogger<ArrivingSoonTicker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly BookingStatus[] _relevant =
        [BookingStatus.Seated, BookingStatus.PendingPayment, BookingStatus.Booked, BookingStatus.Confirmed];

    private readonly WorkerSignal _clockChanged = clockSignal.Subscribe();
    private readonly Dictionary<VenueId, HashSet<TableId>> _last = [];

    /// <summary>One pass: pushes the changed tables per venue and returns how many tables it pushed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var venues = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Select(v => new { v.Id, v.TimeZone })
            .ToListAsync(cancellationToken);
        var now = businessClock.GetUtcNow().ToUniversalTime();
        var pushed = 0;

        foreach (var venue in venues)
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(venue.TimeZone);
            var endOfDay = AvailabilityCalculator.ToUtc(
                AvailabilityCalculator.LocalDate(now, timeZone).AddDays(1), TimeOnly.MinValue, timeZone);
            var bookings = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
                .Where(b => b.VenueId == venue.Id && _relevant.Contains(b.Status))
                .Select(b => new
                {
                    b.Status,
                    b.Period,
                    TableIds = b.Tables.Where(t => t.Active).Select(t => t.TableId).ToList(),
                })
                .ToListAsync(cancellationToken);

            var current = ArrivingSoon(
                bookings.SelectMany(b => b.TableIds.Select(t => (t, new TableBookingView(b.Status, b.Period)))), now, endOfDay);
            if (!_last.TryGetValue(venue.Id, out var previous))
            {
                _last[venue.Id] = current;
                continue;
            }

            var changed = Changed(previous, current);
            if (changed.Count == 0)
            {
                continue;
            }

            var notice = new RealtimeNotice(
                NoticeMethods.FloorPlanChanged, new FloorPlanChangedNotice([.. changed.Select(t => t.Value)], [], 0));
            try
            {
                await broadcaster.SendAsync(venue.Id, notice, cancellationToken);
                _last[venue.Id] = current;
                pushed += changed.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Not recorded, so the next pass tries again.
                LogPushFailed(logger, ex.GetType().Name);
            }
        }

        return pushed;
    }

    /// <summary>The tables whose state is <see cref="TableState.ArrivingSoon"/> at <paramref name="now"/>.</summary>
    internal static HashSet<TableId> ArrivingSoon(
        IEnumerable<(TableId Table, TableBookingView Booking)> bookings, DateTimeOffset now, DateTimeOffset endOfServiceDay) =>
        [.. bookings
            .GroupBy(b => b.Table, b => b.Booking)
            .Where(g => TableStateRule.Derive(g, now, endOfServiceDay) == TableState.ArrivingSoon)
            .Select(g => g.Key)];

    /// <summary>Tables in one set and not the other, in a stable order.</summary>
    internal static IReadOnlyList<TableId> Changed(IReadOnlySet<TableId> previous, IReadOnlySet<TableId> current) =>
        [.. previous.Except(current).Concat(current.Except(previous)).OrderBy(t => t.Value)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunWorkers)
        {
            return;
        }

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

                // The next tick, or at once when the demo clock moves.
                await _clockChanged.WaitAsync(Interval, stoppingToken);
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arriving-soon check: {ExceptionType} while pushing floorPlanChanged; screens refetch on reconnect")]
    private static partial void LogPushFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Arriving-soon check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception error);
}
