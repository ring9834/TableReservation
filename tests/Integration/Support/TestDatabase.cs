using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
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

    /// <summary>The infrastructure clock used by <see cref="Context(VenueId?)"/> for <c>outbox.created_at</c>.</summary>
    public TestClock InfrastructureClock { get; } = new(DemoTime.InfrastructureStart);

    /// <summary>Rung after commits made through <see cref="Context(VenueId?)"/>.</summary>
    public OutboxSignal Signal => _signal ??= new OutboxSignal(InfrastructureClock);

    private OutboxSignal? _signal;

    private static readonly EventEnvelopeSerializer _serializer = new(new EventTypeRegistry());

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

    /// <summary>
    /// A context that sees only <paramref name="venueId"/>'s data (Golden Dragon by default), with the outbox
    /// interceptor (system actor, a new correlation ID per context).
    /// </summary>
    public ReservationsDbContext Context(VenueId? venueId) =>
        new(
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseReservationsDatabase(DataSource)
                .AddInterceptors(new OutboxSaveChangesInterceptor(
                    _serializer, new RequestContext(), Signal, InfrastructureClock, NullLogger<OutboxSaveChangesInterceptor>.Instance))
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

/// <summary>The demo evening used by the tests: Tuesday 24 November 2026, DemoClock at 18:25 Auckland.</summary>
public static class DemoTime
{
    public static readonly DateOnly Tonight = new(2026, 11, 24);

    public static DateTimeOffset Now => At(GoldenDragon.DemoTime);

    /// <summary>Where test infrastructure clocks start: deliberately not the business time, to show which clock is used.</summary>
    public static readonly DateTimeOffset InfrastructureStart = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(TimeOnly time) => GoldenDragon.At(Tonight, time);

    public static DateTimeOffset At(int hour, int minute) => At(new TimeOnly(hour, minute));
}
