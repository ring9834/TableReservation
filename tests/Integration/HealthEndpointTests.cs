using System.Net;

namespace Reservation.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(postgres.ConnectionString);

    [Fact]
    public async Task Live_returns_200()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_200_when_the_database_answers()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
