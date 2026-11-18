using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

/// <summary>
/// The AWS <c>Demo</c> environment (M12a, Design §17.4): <c>appsettings.Demo.json</c> applies, the API migrates and seeds
/// an empty database at start-up as in Development, and it refuses to start without the CloudFront origin secret.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoEnvironmentTests(PostgresFixture postgres)
{
    private const string OriginSecret = "edge_origin_test_only_not_a_secret_00000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_environment_migrates_and_seeds_an_empty_database_at_start_up()
    {
        await using var database = await TestDatabase.CreateAsync(postgres, migrate: false, seed: false);
        await using var factory = new ApiFactory(database.ConnectionString, migrateOnStartup: true)
        {
            Environment = "Demo",
            Settings = new Dictionary<string, string?> { ["Edge:OriginSecret"] = OriginSecret },
        };
        using var client = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);

        await using var db = database.Context();
        Assert.Equal(11, await db.Bookings.CountAsync(Ct));
    }

    [Fact]
    public async Task The_demo_environment_refuses_to_start_without_the_origin_secret()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var factory = new ApiFactory(database.ConnectionString) { Environment = "Demo" };

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateApiClient());

        Assert.Contains("Edge:OriginSecret", error.Message, StringComparison.Ordinal);
    }
}
