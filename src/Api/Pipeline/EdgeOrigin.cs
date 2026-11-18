using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;

namespace Reservation.Api.Pipeline;

/// <summary>
/// Section <c>Edge</c> (Design §17.4, M12a). In AWS every request reaches the API through CloudFront, which adds a
/// secret origin header; only then is the viewer's address (<c>CloudFront-Viewer-Address</c>) trusted as the client IP
/// for rate limits. Locally and in CI no secret is set and nothing changes.
/// </summary>
internal sealed class EdgeOriginOptions
{
    public const string Section = "Edge";

    /// <summary>True in the <c>Demo</c> environment: the API refuses to start without <see cref="OriginSecret"/>.</summary>
    public bool Required { get; set; }

    /// <summary>The value CloudFront sends in <see cref="HeaderName"/> (Secrets Manager in AWS). Unset: the check is off.</summary>
    public string? OriginSecret { get; set; }

    public string HeaderName { get; set; } = "X-Origin-Verify";

    public bool IsActive => !string.IsNullOrEmpty(OriginSecret);

    public bool IsValid => !Required || OriginSecret is { Length: >= 32 };
}

internal static class EdgeOriginRegistration
{
    public static IServiceCollection AddEdgeOrigin(this IServiceCollection services)
    {
        services.AddOptions<EdgeOriginOptions>()
            .BindConfiguration(EdgeOriginOptions.Section)
            .Validate(
                o => o.IsValid,
                "Edge:Required is true, so Edge:OriginSecret must be set to at least 32 characters (Secrets Manager in AWS).")
            .Validate(o => !string.IsNullOrWhiteSpace(o.HeaderName), "Edge:HeaderName must not be empty.")
            .ValidateOnStart();
        return services;
    }
}

/// <summary>
/// With <see cref="EdgeOriginOptions.OriginSecret"/> set: a request without the right origin header gets
/// 403 <c>direct_access_refused</c>, except <c>/health/live</c>, which Lightsail's health check calls directly. A request
/// with it has the header removed (so it is never logged), its client IP taken from <c>CloudFront-Viewer-Address</c>
/// and its scheme set to https (CloudFront terminated TLS for the viewer).
/// </summary>
internal sealed partial class EdgeOriginMiddleware(RequestDelegate next, IOptions<EdgeOriginOptions> options, ILogger<EdgeOriginMiddleware> logger)
{
    public const string ViewerAddressHeader = "CloudFront-Viewer-Address";
    public const string LivePath = "/health/live";

    private readonly EdgeOriginOptions _options = options.Value;
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(options.Value.OriginSecret ?? string.Empty);
    private int _warnedMissingViewer;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.IsActive)
        {
            await next(context);
            return;
        }

        var headers = context.Request.Headers;
        var verified = headers.TryGetValue(_options.HeaderName, out var supplied) && supplied.Count == 1 && Matches(supplied[0]);
        headers.Remove(_options.HeaderName);

        if (!verified)
        {
            if (context.Request.Path.Equals(LivePath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            LogRefused(logger);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = ApiProblems.Create(StatusCodes.Status403Forbidden, ProblemCodes.DirectAccessRefused),
            });
            return;
        }

        context.Request.Scheme = Uri.UriSchemeHttps;
        if (TryParseViewerAddress(headers[ViewerAddressHeader].ToString(), out var viewer))
        {
            context.Connection.RemoteIpAddress = viewer;
        }
        else if (Interlocked.Exchange(ref _warnedMissingViewer, 1) == 0)
        {
            LogNoViewerAddress(logger, ViewerAddressHeader);
        }

        await next(context);
    }

    /// <summary>
    /// <c>CloudFront-Viewer-Address</c> is the viewer's IP and source port joined by a colon: <c>198.51.100.10:46532</c>
    /// or <c>2001:db8::1:46532</c> (IPv6 without brackets), so the port is everything after the last colon.
    /// </summary>
    public static bool TryParseViewerAddress(string? value, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1
            || !int.TryParse(trimmed.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
        {
            return false;
        }

        var host = trimmed[..colon].Trim('[', ']');
        if (!IPAddress.TryParse(host, out var parsed))
        {
            return false;
        }

        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    private bool Matches(string? supplied) =>
        supplied is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), _secret);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request refused: it did not come through the CDN")]
    private static partial void LogRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The CDN sent no usable {Header}; rate limits fall back to the connection address")]
    private static partial void LogNoViewerAddress(ILogger logger, string header);
}
