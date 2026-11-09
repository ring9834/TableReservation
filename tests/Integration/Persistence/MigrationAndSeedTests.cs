using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class MigrationAndSeedTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_apply_to_an_empty_database()
    {
        await using var database = await TestDatabase.CreateAsync(postgres, migrate: false, seed: false);
        await using var db = database.Context();

        await db.Database.MigrateAsync(Ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        Assert.Single(await db.Database.GetAppliedMigrationsAsync(Ct));
        Assert.Equal(1, await Scalar(database, "SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'"));
        Assert.Equal(1, await Scalar(database, "SELECT count(*) FROM pg_constraint WHERE conname = 'ex_table_assignments_no_overlap' AND contype = 'x'"));
        Assert.Equal(1, await Scalar(database, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_bookings_venue_day'"));
        Assert.Equal(2, await Scalar(database, "SELECT count(*) FROM pg_indexes WHERE indexname IN ('ux_booking_bills_one_per_booking', 'ux_booking_bills_venue_pos_bill')"));
    }

    [Fact]
    public async Task Seed_loads_Golden_Dragon()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var db = database.Context();

        var venue = await db.Venues.SingleAsync(Ct);

        Assert.Equal("Golden Dragon", venue.Name);
        Assert.Equal(2, venue.Areas.Count);
        Assert.Equal(14, venue.Tables.Count());
        Assert.Equal(11, await db.Bookings.CountAsync(Ct));
        Assert.Equal(11, await db.Guests.CountAsync(Ct));
        Assert.Equal(0, await db.Outbox.CountAsync(Ct));
    }

    [Fact]
    public async Task Api_start_up_in_Development_migrates_and_seeds_a_fresh_database()
    {
        await using var database = await TestDatabase.CreateAsync(postgres, migrate: false, seed: false);
        await using var factory = new ApiFactory(database.ConnectionString, migrateOnStartup: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = database.Context();
        Assert.Equal(14, await db.Venues.SelectMany(v => v.Areas).SelectMany(a => a.Tables).CountAsync(Ct));
        Assert.Equal(11, await db.Bookings.CountAsync(Ct));
    }

    private static async Task<long> Scalar(TestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
