using System.Net;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private ApiFactory _factory = null!;

    [Fact]
    public async Task Live_returns_200()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_200_when_the_database_and_outbox_answer()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_200_when_degraded_by_a_failed_outbox_row()
    {
        await using (var command = _database.DataSource.CreateCommand(
            "INSERT INTO outbox (event_id, venue_id, event_type, event_version, aggregate_type, aggregate_id, sequence, " +
            "correlation_id, envelope, status, attempts, next_attempt_at, created_at) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), 'BookingCreated', 1, 'Booking', gen_random_uuid(), 1, " +
            "gen_random_uuid(), '{}', 'Failed', 52, now(), now())"))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_returns_503_when_unhealthy()
    {
        // Database answers, but has no outbox table: the outbox check is Unhealthy.
        await using var empty = await TestDatabase.CreateAsync(postgres, migrate: false, seed: false);
        await using var factory = new ApiFactory(empty.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(postgres, seed: false);
        _factory = new ApiFactory(_database.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _database.DisposeAsync();
    }
}
