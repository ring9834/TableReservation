using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;

namespace Reservation.Api.Pipeline;

/// <summary>
/// Per-minute limits (ADR-004 rule 5), section <c>RateLimits</c>, all configurable. Since M10b (Design §18.10) anonymous
/// traffic is split into buckets, because a presenter laptop, a guest phone and a host tablet on venue Wi-Fi share one
/// public IP: the booking-status poll counts per booking, guest-link calls per link, the rest per IP.
/// </summary>
internal sealed class ApiRateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Every other anonymous route (availability, venue, payment config, online booking), per client IP.</summary>
    public int AnonymousPerMinute { get; set; } = 300;

    /// <summary>The guest's payment-status poll, per booking (a poll every 2 s is 30 a minute).</summary>
    public int BookingStatusPerMinute { get; set; } = 60;

    /// <summary>The guest-link endpoints (open, confirm, cancel), per link (a hash of the token).</summary>
    public int GuestLinkPerMinute { get; set; } = 30;

    /// <summary>
    /// All anonymous traffic from one IP together, whichever bucket it is in: a ceiling so that spraying made-up booking
    /// IDs or tokens (each its own bucket) stays bounded.
    /// </summary>
    public int AnonymousCeilingPerMinute { get; set; } = 600;

    /// <summary>Online booking attempts, per IP (on top of the anonymous bucket).</summary>
    public int BookingPerMinute { get; set; } = 10;

    /// <summary>Demo sign-in attempts, per IP.</summary>
    public int DemoLoginPerMinute { get; set; } = 5;
}

/// <summary>Which anonymous bucket a route counts in, when it isn't the per-IP one (endpoint metadata).</summary>
internal enum AnonymousBucket
{
    /// <summary>Per booking: the <c>id</c> route value.</summary>
    BookingStatus,

    /// <summary>Per link: a hash of the guest-link token (<see cref="GuestLinkRateKeyMiddleware"/>).</summary>
    GuestLink,
}

internal sealed record AnonymousBucketMetadata(AnonymousBucket Bucket);

internal static class AnonymousBucketExtensions
{
    /// <summary>Counts the route's anonymous calls in their own bucket instead of the per-IP one (Design §18.10).</summary>
    public static RouteHandlerBuilder WithAnonymousBucket(this RouteHandlerBuilder builder, AnonymousBucket bucket) =>
        builder.WithMetadata(new AnonymousBucketMetadata(bucket));
}

/// <summary>
/// Rate limiting (ADR-004 rule 5). The global limiter applies to every endpoint that allows anonymous callers, chained:
/// <list type="number">
/// <item>a per-IP ceiling over all of them (<see cref="ApiRateLimitOptions.AnonymousCeilingPerMinute"/>);</item>
/// <item>the route's own bucket: the booking-status poll per booking, the guest-link endpoints per link (a SHA-256 of
/// the token, never the token itself; a request without a readable token falls back to the per-IP bucket), and every
/// other anonymous route per IP (<see cref="ApiRateLimitOptions.AnonymousPerMinute"/>).</item>
/// </list>
/// Signed-in-only routes are not limited. The <see cref="Booking"/> and <see cref="DemoLogin"/> policies add stricter
/// per-IP limits on top, so the stricter one wins. Health checks and the OpenAPI document opt out
/// (<c>DisableRateLimiting</c>), so probes never see 429. A rejection is 429 <c>too_many_requests</c> with
/// <c>Retry-After</c>; the log names the endpoint only. Behind CloudFront the client IP must come from forwarded headers
/// (Design §20, M12).
/// </summary>
internal static partial class RateLimits
{
    public const string Booking = "booking";
    public const string DemoLogin = "demo-login";

    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<ApiRateLimitOptions>()
            .BindConfiguration(ApiRateLimitOptions.Section)
            .Validate(
                o => o.AnonymousPerMinute > 0 && o.BookingStatusPerMinute > 0 && o.GuestLinkPerMinute > 0
                     && o.AnonymousCeilingPerMinute > 0 && o.BookingPerMinute > 0 && o.DemoLoginPerMinute > 0,
                "Rate limits must be positive.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ApiRateLimitOptions>>((options, limits) =>
        {
            var l = limits.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var ceiling = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsAnonymous(context)
                    ? RateLimitPartition.GetFixedWindowLimiter($"ceiling:{ClientKey(context)}", _ => Window(l.AnonymousCeilingPerMinute))
                    : RateLimitPartition.GetNoLimiter("signed-in"));
            var bucket = PartitionedRateLimiter.Create<HttpContext, string>(context => BucketFor(context, l));
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(ceiling, bucket);
            options.AddPolicy(Booking, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(l.BookingPerMinute)));
            options.AddPolicy(DemoLogin, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(l.DemoLoginPerMinute)));
            options.OnRejected = OnRejectedAsync;
        });
        return services;
    }

    private static bool IsAnonymous(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static RateLimitPartition<string> BucketFor(HttpContext context, ApiRateLimitOptions l)
    {
        if (!IsAnonymous(context))
        {
            return RateLimitPartition.GetNoLimiter("signed-in");
        }

        switch (context.GetEndpoint()?.Metadata.GetMetadata<AnonymousBucketMetadata>()?.Bucket)
        {
            case AnonymousBucket.BookingStatus when context.Request.RouteValues["id"] is string bookingId:
                return RateLimitPartition.GetFixedWindowLimiter($"status:{bookingId.ToUpperInvariant()}", _ => Window(l.BookingStatusPerMinute));
            case AnonymousBucket.GuestLink when GuestLinkRateKeyMiddleware.KeyOf(context) is { } linkKey:
                return RateLimitPartition.GetFixedWindowLimiter($"link:{linkKey}", _ => Window(l.GuestLinkPerMinute));
            default:
                return RateLimitPartition.GetFixedWindowLimiter($"anonymous:{ClientKey(context)}", _ => Window(l.AnonymousPerMinute));
        }
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = _window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : _window;
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimits).FullName!);
        LogRejected(logger, http.GetEndpoint()?.DisplayName ?? "(unmatched)");

        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = ApiProblems.Create(StatusCodes.Status429TooManyRequests, ProblemCodes.TooManyRequests),
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit hit on {Endpoint}")]
    private static partial void LogRejected(ILogger logger, string endpoint);
}
