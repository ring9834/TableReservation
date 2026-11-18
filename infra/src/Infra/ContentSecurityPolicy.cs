namespace Reservation.Infra;

/// <summary>
/// The demo's Content-Security-Policy (Design §17.4): only our origin plus Stripe. In report mode the header also
/// carries <c>script-src</c> with the pages' hashes; in enforce mode each page's own <c>&lt;meta&gt;</c> carries it
/// (web/tools/csp-hashes.mjs), so the header leaves it out.
/// </summary>
public static class ContentSecurityPolicy
{
    /// <summary>CloudFront's limit on the CSP value in a response headers policy.</summary>
    public const int MaxLength = 1783;

    public const string ReportOnlyHeader = "Content-Security-Policy-Report-Only";

    // No default-src: it would also apply to scripts in enforce mode and block the inline ones each page's meta allows by
    // hash (every policy must pass). So each fetch directive is listed explicitly instead, the same in both modes.
    private static readonly string[] _directives =
    [
        "base-uri 'self'",
        "object-src 'none'",
        "frame-ancestors 'self'", // the presenter's phone panel frames /manage/ (Design §18.10)
        "form-action 'self'",
        "img-src 'self' data:",
        "style-src 'self'",
        "font-src 'self'",
        "media-src 'self'",
        "connect-src 'self' https://api.stripe.com", // 'self' covers wss: to the same host for SignalR
        "frame-src 'self' https://js.stripe.com https://hooks.stripe.com",
        "manifest-src 'self'",
        "worker-src 'none'",
    ];

    /// <summary>
    /// The header value. <paramref name="scriptSources"/> is used in report mode only; empty means <c>'self'</c> and
    /// Stripe.js without hashes.
    /// </summary>
    public static string Build(CspMode mode, IReadOnlyList<string> scriptSources)
    {
        ArgumentNullException.ThrowIfNull(scriptSources);
        IReadOnlyList<string> scripts = scriptSources.Count > 0 ? scriptSources : ["'self'", "https://js.stripe.com"];
        List<string> directives = mode == CspMode.Enforce
            ? [.. _directives]
            : [.. _directives, "script-src " + string.Join(' ', scripts)];

        var value = string.Join("; ", directives);
        if (value.Length > MaxLength)
        {
            throw new InvalidOperationException(
                $"The CSP is {value.Length} characters; CloudFront allows {MaxLength}. Too many inline-script hashes for the report-only header?");
        }

        return value;
    }
}
