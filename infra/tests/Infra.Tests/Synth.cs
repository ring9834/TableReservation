using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.Assertions;

// CDK runs in one jsii (Node) process per test host; parallel synths stall it, and each synth costs seconds. So tests
// run one at a time (xunit.runner.json) and share the synthesized templates per configuration.

namespace Reservation.Infra.Tests;

/// <summary>Synthesizes both stacks for a configuration, as <c>cdk synth</c> would, without AWS credentials.</summary>
internal static class Synth
{
    public const string ApiHost = "reservation-api.abc123def456.ap-southeast-2.cs.amazonlightsail.com";

    private static readonly ConcurrentDictionary<string, (Template Permanent, Template App)> _templates = new();
    private static readonly ConditionalWeakTable<Template, string> _json = [];

    public static DemoConfig Full => new()
    {
        ApiOrigin = ApiHost,
        BudgetEmail = "budget@example.org",
        ImageName = ":reservation-api.api.3",
        DeployId = "20261117-1200",
    };

    public static (Template Permanent, Template App) Templates(DemoConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return _templates.GetOrAdd(Key(config), _ =>
        {
            var app = new App();
            var (permanent, appStack) = DemoApp.Build(app, config);
            return (Template.FromStack(permanent), Template.FromStack(appStack));
        });
    }

    public static JsonElement Json(Template template) =>
        JsonDocument.Parse(_json.GetValue(template, t => JsonSerializer.Serialize(t.ToJSON()))).RootElement.Clone();

    /// <summary>Every resource of a type, as JSON.</summary>
    public static IReadOnlyList<JsonElement> Resources(Template template, string type) =>
        [.. Json(template).GetProperty("Resources").EnumerateObject()
            .Select(r => r.Value)
            .Where(r => r.GetProperty("Type").GetString() == type)];

    public static JsonElement Single(Template template, string type) => Assert.Single(Resources(template, type));

    private static string Key(DemoConfig config) => JsonSerializer.Serialize(new
    {
        config.ApiOrigin,
        Csp = config.CspMode.ToString(),
        config.CspScriptSources,
        config.BudgetEmail,
        config.ImageName,
        config.DeployId,
        config.LightsailCidr,
    });
}
