using System.Net;
using System.Text.Json;

namespace Reservation.Tests.Integration.Api;

/// <summary>Checks the ADR-004 Problem Details shape: type, title, status, code, traceId and errors.</summary>
public static class ProblemAssert
{
    public static async Task<JsonElement> IsAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal($"https://errors.example.app/{code.Replace('_', '-')}", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.True(problem.TryGetProperty("errors", out var errors), "Problem Details must carry 'errors' (null unless 400).");
        if (status != HttpStatusCode.BadRequest)
        {
            Assert.Equal(JsonValueKind.Null, errors.ValueKind);
        }

        Assert.False(problem.TryGetProperty("detail", out _), "Problem Details must not carry 'detail'.");
        Assert.False(problem.TryGetProperty("exception", out _));
        return problem;
    }

    /// <summary>A 400 with the given rule code on the given field.</summary>
    public static async Task IsValidationAsync(HttpResponseMessage response, string field, string ruleCode)
    {
        var problem = await IsAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out var codes), $"No errors for '{field}': {errors}");
        Assert.Contains(ruleCode, codes.EnumerateArray().Select(c => c.GetString()));
    }
}
