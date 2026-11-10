using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reservation.Api.Pipeline;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// The cross-cutting pipeline (ADR-004): Problem Details for the codes no booking route reaches, anonymous health
/// and OpenAPI, the anonymous rate limit and the central mapping of infrastructure exceptions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PipelineTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override void ConfigureServices(IServiceCollection services) => TestEndpoints.Add(services);

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    public async Task Health_and_openapi_answer_without_a_session(string path)
    {
        var response = await Client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_checks_are_never_rate_limited()
    {
        for (var i = 0; i < 70; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/live", Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task The_61st_anonymous_call_in_a_minute_is_too_many_requests()
    {
        var path = $"{Venue}/availability?date={DemoTime.Tonight:yyyy-MM-dd}&partySize=2";
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(path, Ct)).StatusCode);
        }

        var response = await Client.GetAsync(path, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Hold_expired_is_a_409_problem()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync("/api/v1/test/hold-expired", Ct), HttpStatusCode.Conflict, "hold_expired");
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_500_without_internals_and_is_logged_once()
    {
        var response = await Client.GetAsync("/api/v1/test/boom", Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.InternalServerError, "unexpected");
        Assert.DoesNotContain(TestEndpoints.Secret, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Single(Factory.Logs.Lines, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task An_unknown_route_is_401_when_anonymous_and_a_404_problem_when_signed_in()
    {
        // Deny by default: the fallback policy also covers paths no endpoint matches, so they reveal nothing.
        await ProblemAssert.IsAsync(await Client.GetAsync("/api/v1/nothing-here", Ct), HttpStatusCode.Unauthorized, "not_signed_in");

        await SignInAsync(Client);

        await ProblemAssert.IsAsync(await Client.GetAsync("/api/v1/nothing-here", Ct), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public void Infrastructure_exceptions_map_to_their_codes()
    {
        var exclusion = new PostgresException("conflict", "ERROR", "ERROR", PostgresErrorCodes.ExclusionViolation);

        Assert.Equal((409, "slot_taken"), GlobalExceptionHandler.Classify(new DbUpdateException("save", exclusion)));
        Assert.Equal((409, "version_conflict"), GlobalExceptionHandler.Classify(new DbUpdateConcurrencyException("stale")));
        Assert.Equal((500, "unexpected"), GlobalExceptionHandler.Classify(new DbUpdateException("other")));
    }
}
