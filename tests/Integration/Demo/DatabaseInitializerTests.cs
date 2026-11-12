using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

/// <summary>Development start-up (D8) and <c>Demo:ResetIfStale</c> (Design §17).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseInitializerTests(PostgresFixture postgres)
{
    private const string ResetLine = "Demo data had no bookings today; reset and reseeded for today";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stale_data_is_reset_for_today_when_ResetIfStale_is_on()
    {
        await using var database = await TestDatabase.CreateAsync(postgres); // seeded for DemoTime.Tonight
        var nextDay = DemoTime.Now.AddDays(1);
        var logs = new LogCapture();

        await InitializeAsync(database, nextDay, resetIfStale: true, logs);

        await using var db = database.Context();
        var starts = await db.Bookings.Select(b => b.Period).ToListAsync(Ct);
        Assert.Equal(11, starts.Count);
        Assert.All(starts, p => Assert.Equal(DemoTime.Tonight.AddDays(1), GoldenDragon.ServiceDate(p.Start)));
        var line = Assert.Single(logs.Lines, l => l.Text.StartsWith(ResetLine, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
    }

    [Fact]
    public async Task Data_for_today_is_kept()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await AddMarkerAsync(database);
        var logs = new LogCapture();

        await InitializeAsync(database, DemoTime.Now, resetIfStale: true, logs);

        Assert.True(await HasMarkerAsync(database));
        Assert.DoesNotContain(logs.Lines, l => l.Text.StartsWith(ResetLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_data_is_kept_when_ResetIfStale_is_off()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await AddMarkerAsync(database);
        var logs = new LogCapture();

        await InitializeAsync(database, DemoTime.Now.AddDays(1), resetIfStale: false, logs);

        Assert.True(await HasMarkerAsync(database));
        Assert.DoesNotContain(logs.Lines, l => l.Text.StartsWith(ResetLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_database_is_seeded_for_today()
    {
        await using var database = await TestDatabase.CreateAsync(postgres, migrate: false, seed: false);
        var nextDay = DemoTime.Now.AddDays(1);

        await InitializeAsync(database, nextDay, resetIfStale: true, new LogCapture());

        await using var db = database.Context();
        var starts = await db.Bookings.Select(b => b.Period).ToListAsync(Ct);
        Assert.Equal(11, starts.Count);
        Assert.All(starts, p => Assert.Equal(DemoTime.Tonight.AddDays(1), GoldenDragon.ServiceDate(p.Start)));
    }

    [Fact]
    public void Fixed_mode_replaces_only_the_business_clock()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Clock:Mode"] = "Fixed",
                ["Demo:Clock:FixedLocalTime"] = "18:25",
            })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure("Host=localhost;Database=unused");
        using var provider = services.BuildServiceProvider();

        var business = Assert.IsType<DemoClock>(provider.GetRequiredService<TimeProvider>());
        Assert.Equal(new TimeOnly(18, 25), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(business.Now, GoldenDragon.TimeZone()).DateTime));
        Assert.Same(TimeProvider.System, provider.GetRequiredKeyedService<TimeProvider>(Clocks.Infrastructure));
    }

    [Fact]
    public void Fixed_mode_without_a_time_fails_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Clock:Mode"] = "Fixed" })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure("Host=localhost;Database=unused");
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DemoClockOptions>>().Value);
    }

    private static async Task InitializeAsync(TestDatabase database, DateTimeOffset now, bool resetIfStale, LogCapture logs)
    {
        await using var db = database.Context();
        var clock = new TestClock(now);
        var seeder = new DemoSeeder(clock);
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var initializer = new DatabaseInitializer(
            db, seeder, new DemoReset(db, seeder), clock,
            Options.Create(new DemoDataOptions { ResetIfStale = resetIfStale }),
            loggerFactory.CreateLogger<DatabaseInitializer>());

        await initializer.InitializeAsync(Ct);
    }

    // A change made after the seed: still there means no reset ran.
    private static async Task AddMarkerAsync(TestDatabase database)
    {
        await using var db = database.Context();
        await db.Database.ExecuteSqlRawAsync("UPDATE venues SET name = 'Golden Dragon (kept)'", Ct);
    }

    private static async Task<bool> HasMarkerAsync(TestDatabase database)
    {
        await using var db = database.Context();
        return await db.Venues.AnyAsync(v => v.Name == "Golden Dragon (kept)", Ct);
    }
}
