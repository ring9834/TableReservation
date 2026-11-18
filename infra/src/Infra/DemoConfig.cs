using System.Text.Json;
using Constructs;

namespace Reservation.Infra;

/// <summary>How the demo's CSP is sent (Design §17.4): report-only first, enforced once the console is clean.</summary>
public enum CspMode
{
    Report,
    Enforce,
}

/// <summary>
/// Everything the stacks take from outside, as CDK context (<c>-c Name=value</c>). Nothing secret: secrets stay in
/// Secrets Manager and reach the container as CloudFormation dynamic references.
/// </summary>
public sealed record DemoConfig
{
    public const string Account = "821797257125";
    public const string Region = "ap-southeast-2";
    public const string Project = "reservation-demo";

    /// <summary>The Lightsail container service's public host (no scheme); without it the distribution has no API routes.</summary>
    public string? ApiOrigin { get; init; }

    public CspMode CspMode { get; init; } = CspMode.Report;

    /// <summary>
    /// The script sources for the report-only header, from <c>web/out/csp-script-hashes.json</c> (built with
    /// <c>CSP_MODE=report</c>). Empty: only <c>'self'</c> and Stripe, and every inline script is reported.
    /// </summary>
    public IReadOnlyList<string> CspScriptSources { get; init; } = [];

    /// <summary>Where the budget alerts go. Passed at deploy time, never committed; no budget without it.</summary>
    public string? BudgetEmail { get; init; }

    /// <summary>The pushed image, e.g. <c>:reservation-api.api.3</c>; without it the service has no deployment.</summary>
    public string? ImageName { get; init; }

    /// <summary>Changes the deployment so CloudFormation resolves the secrets again (after a secret changed).</summary>
    public string DeployId { get; init; } = "initial";

    /// <summary>The Lightsail VPC's range, the only source the database accepts (the peering's requester CIDR).</summary>
    public string LightsailCidr { get; init; } = "172.26.0.0/16";

    public static DemoConfig From(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string? Get(string key) => node.TryGetContext(key) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var mode = Get("CspMode") switch
        {
            null or "report" => CspMode.Report,
            "enforce" => CspMode.Enforce,
            var other => throw new ArgumentException($"CspMode must be 'report' or 'enforce', not '{other}'."),
        };

        return new DemoConfig
        {
            ApiOrigin = NormalizeHost(Get("ApiOrigin")),
            CspMode = mode,
            CspScriptSources = Get("CspScriptHashesFile") is { } file ? ReadScriptSources(file) : [],
            BudgetEmail = Get("BudgetEmail"),
            ImageName = Get("ImageName"),
            DeployId = Get("DeployId") ?? "initial",
            LightsailCidr = Get("LightsailCidr") ?? "172.26.0.0/16",
        };
    }

    /// <summary>Accepts the host as Lightsail prints it (<c>https://…/</c>) and keeps the host only.</summary>
    public static string? NormalizeHost(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var host = value.Trim();
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = host["https://".Length..];
        }

        host = host.TrimEnd('/');
        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) || host.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"ApiOrigin must be a host name such as reservation-api.xxxx.ap-southeast-2.cs.amazonlightsail.com, not '{value}'.");
        }

        return host;
    }

    /// <summary>Reads <c>{"scriptSrc": ["'self'", "https://js.stripe.com", "'sha256-…'", …]}</c>.</summary>
    public static IReadOnlyList<string> ReadScriptSources(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var sources = document.RootElement.GetProperty("scriptSrc").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        foreach (var source in sources)
        {
            if (source is not ("'self'" or "https://js.stripe.com") && !(source.StartsWith("'sha256-", StringComparison.Ordinal) && source.EndsWith('\'')))
            {
                throw new ArgumentException($"Unexpected script source '{source}' in {path}.");
            }
        }

        return sources;
    }
}
