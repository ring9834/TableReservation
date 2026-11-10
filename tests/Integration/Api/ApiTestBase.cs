using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// One seeded database (Golden Dragon at 18:25) and one in-memory API per test. Subclasses can add settings and
/// services, e.g. <see cref="TestEndpoints"/>.
/// </summary>
public abstract class ApiTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    public static readonly string Venue = $"/api/v1/venues/{GoldenDragon.VenueId.Value}";

    /// <summary>A venue the demo session does not work at.</summary>
    public static readonly string OtherVenue = $"/api/v1/venues/{Guid.Parse("0193a7d0-0000-7000-8000-0000000000ff")}";

    protected TestDatabase Database { get; private set; } = null!;

    protected ApiFactory Factory { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Tomorrow evening in Auckland: no seeded bookings.</summary>
    protected static DateOnly Tomorrow => DemoTime.Tonight.AddDays(1);

    protected virtual IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>();

    public virtual async ValueTask InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync(postgres);
        Factory = new ApiFactory(Database.ConnectionString) { Settings = Settings, ExtraServices = ConfigureServices };
        Client = Factory.CreateApiClient();
    }

    public virtual async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected HttpClient NewClient() => Factory.CreateApiClient();

    protected static async Task SignInAsync(HttpClient client, string passcode = ApiFactory.HostPasscode)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.PostAsJsonAsync("/api/v1/demo-login", new { passcode }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    protected static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? idempotencyKey = null, Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiJson.Options) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId.Value.ToString());
        }

        return client.SendAsync(request, Ct);
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, ApiJson.Options)!;
    }

    /// <summary>A booking request body in the API's JSON shape. Times are venue-local on <paramref name="date"/>.</summary>
    protected static object BookingBody(
        DateOnly date, int hour, int minute, int partySize, string name = "Test Guest", string mobile = "021 011 0199",
        string? email = null, string? notes = null, string? allergyNotes = null, string? occasion = null, string language = "en") =>
        new
        {
            startsAt = GoldenDragon.At(date, new TimeOnly(hour, minute)),
            partySize,
            name,
            mobile,
            email,
            allergyNotes,
            occasion,
            notes,
            language,
        };
}

/// <summary>The API's JSON settings: camelCase, enums as camelCase strings.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
