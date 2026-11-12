using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Development only (D8): applies migrations and loads the seed into an empty database. With
/// <see cref="DemoDataOptions.ResetIfStale"/>, a database whose bookings are all on other days than today (venue-local
/// date on the business clock) is reset and reseeded, so a local database seeded on an earlier day shows tonight again
/// (Design §17). CI and AWS run the EF Core migration bundle instead (ADR-005).
/// </summary>
public sealed partial class DatabaseInitializer(
    ReservationsDbContext db,
    DemoSeeder seeder,
    DemoReset reset,
    TimeProvider clock,
    IOptions<DemoDataOptions> options,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        LogMigrated(logger);

        var timeZoneId = await db.Venues.IgnoreQueryFilters().OrderBy(v => v.Id).Select(v => v.TimeZone)
            .FirstOrDefaultAsync(cancellationToken);
        if (timeZoneId is null)
        {
            await seeder.SeedAsync(db, cancellationToken);
            LogSeeded(logger);
            return;
        }

        if (options.Value.ResetIfStale && !await HasBookingsTodayAsync(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId), cancellationToken))
        {
            await reset.ResetAsync(cancellationToken);
            LogResetStale(logger);
        }
    }

    private async Task<bool> HasBookingsTodayAsync(TimeZoneInfo timeZone, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).DateTime);
        var from = ToUtc(today, timeZone);
        var to = ToUtc(today.AddDays(1), timeZone);
        return await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM bookings WHERE lower(period) >= {from} AND lower(period) < {to}) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    private static DateTime ToUtc(DateOnly date, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), timeZone);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Golden Dragon demo seed loaded")]
    private static partial void LogSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo data had no bookings today; reset and reseeded for today")]
    private static partial void LogResetStale(ILogger logger);
}
