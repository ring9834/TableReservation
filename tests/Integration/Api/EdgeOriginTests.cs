using System.Net;
using System.Net.Http.Json;
using Reservation.Api.Common;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// The CloudFront origin check and the viewer's address (M12a, Design §17.4): with <c>Edge:OriginSecret</c> set, only
/// requests carrying the secret origin header are served (and <c>/health/live</c> for Lightsail), and the client IP for
/// rate limits comes from <c>CloudFront-Viewer-Address</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EdgeOriginTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Secret = "edge_origin_test_only_not_a_secret_00000";
    private const string Header = "X-Origin-Verify";
    private const string Login = "/api/v1/demo-login";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Edge:Required"] = "true",
        ["Edge:OriginSecret"] = Secret,
    };

    [Fact]
    public async Task A_request_without_the_origin_header_is_refused()
    {
        var response = await Client.GetAsync(Venue, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.Forbidden, ProblemCodes.DirectAccessRefused);
    }

    [Fact]
    public async Task A_request_with_a_wrong_origin_header_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Venue);
        request.Headers.Add(Header, Secret + "x");

        var response = await Client.SendAsync(request, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.Forbidden, ProblemCodes.DirectAccessRefused);
    }

    [Fact]
    public async Task The_liveness_check_answers_without_the_header_but_readiness_does_not()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/health/live", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync("/health/ready", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_request_through_the_cdn_is_served()
    {
        var response = await Client.SendAsync(ThroughCdn(HttpMethod.Get, Venue, "198.51.100.10:46532"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Each_viewer_address_has_its_own_rate_limit_bucket()
    {
        const string viewerA = "198.51.100.10:46532";
        const string viewerB = "2001:db8::7:51000";
        for (var i = 0; i < 5; i++)
        {
            var attempt = await Client.SendAsync(WrongPasscode(viewerA), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var sixth = await Client.SendAsync(WrongPasscode(viewerA), Ct);
        var otherViewer = await Client.SendAsync(WrongPasscode(viewerB), Ct);

        await ProblemAssert.IsAsync(sixth, HttpStatusCode.TooManyRequests, ProblemCodes.TooManyRequests);
        Assert.Equal(HttpStatusCode.Unauthorized, otherViewer.StatusCode);
    }

    [Fact]
    public async Task The_origin_secret_never_reaches_the_logs()
    {
        await Client.SendAsync(ThroughCdn(HttpMethod.Get, Venue, "198.51.100.10:46532"), Ct);
        await Client.SendAsync(WrongPasscode("198.51.100.11:40000"), Ct);
        await Client.GetAsync(Venue, Ct); // refused

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("Request refused: it did not come through the CDN", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100", log, StringComparison.Ordinal);
    }

    private static HttpRequestMessage ThroughCdn(HttpMethod method, string path, string viewer)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(Header, Secret);
        request.Headers.Add("CloudFront-Viewer-Address", viewer);
        return request;
    }

    private static HttpRequestMessage WrongPasscode(string viewer)
    {
        var request = ThroughCdn(HttpMethod.Post, Login, viewer);
        request.Content = JsonContent.Create(new { passcode = "not-the-passcode" });
        return request;
    }
}
