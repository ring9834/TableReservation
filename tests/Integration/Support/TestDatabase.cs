using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Tests.Integration.Support;

/// <summary>A fresh database for one test, migrated and (by default) seeded, with helpers to open contexts.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string connectionString)
    {
        ConnectionString = connectionString;
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public string ConnectionString { get; }

    public NpgsqlDataSource DataSource { get; }

    public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres, bool migrate = true, bool seed = true)
    {
        var database = new TestDatabase(await postgres.CreateDatabaseAsync());
        if (migrate)
        {
            await using var db = database.Context();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        if (seed)
        {
            await database.SeedAsync(DemoTime.Now);
        }

        return database;
    }

    /// <summary>A context that sees only <paramref name="venueId"/>'s data (Golden Dragon by default).</summary>
    public ReservationsDbContext Context(VenueId? venueId) =>
        new(
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseReservationsDatabase(DataSource)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            new FixedVenueContext(venueId));

    public ReservationsDbContext Context() => Context(GoldenDragon.VenueId);

    public async Task SeedAsync(DateTimeOffset now)
    {
        await using var db = Context();
        await new DemoSeeder(new TestClock(now)).SeedAsync(db, TestContext.Current.CancellationToken);
    }

    public async Task ResetAsync(DateTimeOffset now)
    {
        await using var db = Context();
        await new DemoReset(db, new DemoSeeder(new TestClock(now))).ResetAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>A clock fixed at one instant.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The demo evening used by the tests: Tuesday 24 November 2026, DemoClock at 18:25 Auckland.</summary>
public static class DemoTime
{
    public static readonly DateOnly Tonight = new(2026, 11, 24);

    public static DateTimeOffset Now => At(GoldenDragon.DemoTime);

    public static DateTimeOffset At(TimeOnly time) => GoldenDragon.At(Tonight, time);

    public static DateTimeOffset At(int hour, int minute) => At(new TimeOnly(hour, minute));
}
