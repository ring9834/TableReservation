using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Auth;

namespace Reservation.Tests.Integration.Api;

/// <summary>Demo sign-in and the demo session (Design §14, §17; ADR-004 Security).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoLoginTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Login = "/api/v1/demo-login";

    private static readonly string PresenterOnly = $"/api/v1/test/venues/{Infrastructure.Demo.GoldenDragon.VenueId.Value}/presenter-only";

    protected override void ConfigureServices(IServiceCollection services) => TestEndpoints.Add(services);

    [Fact]
    public async Task The_host_passcode_issues_a_strict_http_only_secure_cookie_for_12_hours()
    {
        var response = await Client.PostAsJsonAsync(Login, new { passcode = ApiFactory.HostPasscode }, Ct);

        var body = await ReadAsync<DemoLoginResponse>(response);
        Assert.Equal(StaffRole.Host, body.Role);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var attributes = cookie.Split(';', StringSplitOptions.TrimEntries);
        Assert.StartsWith("__Host-demo-session=", attributes[0], StringComparison.Ordinal);
        Assert.Contains("httponly", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("secure", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", attributes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("path=/", attributes, StringComparer.OrdinalIgnoreCase);
        var expires = DateTimeOffset.Parse(
            attributes.Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        // Infrastructure clock (real time), not the demo clock.
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddHours(12).AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(12).AddMinutes(1));
    }

    [Fact]
    public async Task The_presenter_passcode_gives_presenter_which_also_passes_host_endpoints()
    {
        var body = await ReadAsync<DemoLoginResponse>(await Client.PostAsJsonAsync(Login, new { passcode = ApiFactory.PresenterPasscode }, Ct));

        Assert.Equal(StaffRole.Presenter, body.Role);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(Venue + "/bookings", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.GetAsync(PresenterOnly, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_passcode_is_not_signed_in_and_sets_no_cookie()
    {
        var response = await Client.PostAsJsonAsync(Login, new { passcode = "wrong" }, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.Unauthorized, "not_signed_in");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await ProblemAssert.IsAsync(await Client.GetAsync(Venue + "/bookings", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    [Fact]
    public async Task An_empty_passcode_is_validation_failed()
    {
        var response = await Client.PostAsJsonAsync(Login, new { passcode = "" }, Ct);

        await ProblemAssert.IsValidationAsync(response, "passcode", "passcode_required");
    }

    [Fact]
    public async Task The_sixth_attempt_in_a_minute_is_too_many_requests_with_retry_after()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync(Login, new { passcode = "wrong" }, Ct)).StatusCode);
        }

        var response = await Client.PostAsJsonAsync(Login, new { passcode = ApiFactory.HostPasscode }, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_guest_cannot_call_host_endpoints()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync(Venue + "/bookings", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Venue}/bookings/{Guid.CreateVersion7()}/cancel", new { version = 1 }), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    [Fact]
    public async Task A_host_cannot_call_presenter_endpoints()
    {
        await SignInAsync(Client);

        await ProblemAssert.IsAsync(await Client.GetAsync(PresenterOnly, Ct), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_host_cannot_reach_another_venue()
    {
        await SignInAsync(Client);

        await ProblemAssert.IsAsync(await Client.GetAsync(OtherVenue + "/bookings", Ct), HttpStatusCode.Forbidden, "forbidden");
    }
}
