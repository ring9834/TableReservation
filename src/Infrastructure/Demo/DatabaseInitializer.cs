using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Development only (D8): applies migrations and loads the seed into an empty database.
/// CI and AWS run the EF Core migration bundle instead (ADR-005).
/// </summary>
public sealed partial class DatabaseInitializer(ReservationsDbContext db, DemoSeeder seeder, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        LogMigrated(logger);

        if (await db.Venues.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        await seeder.SeedAsync(db, cancellationToken);
        LogSeeded(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Golden Dragon demo seed loaded")]
    private static partial void LogSeeded(ILogger logger);
}
