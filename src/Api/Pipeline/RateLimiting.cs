using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;

namespace Reservation.Api.Pipeline;

/// <summary>Per-minute limits per client IP (ADR-004 rule 5), section <c>RateLimits</c>.</summary>
internal sealed class ApiRateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Every anonymous endpoint (availability, online booking, demo sign-in).</summary>
    public int AnonymousPerMinute { get; set; } = 60;

    /// <summary>Online booking attempts.</summary>
    public int BookingPerMinute { get; set; } = 10;

    /// <summary>Demo sign-in attempts.</summary>
    public int DemoLoginPerMinute { get; set; } = 5;
}

/// <summary>
/// Rate limiting (ADR-004 rule 5). A global limiter applies <see cref="ApiRateLimitOptions.AnonymousPerMinute"/> to
/// every endpoint that allows anonymous callers; the <see cref="Booking"/> and <see cref="DemoLogin"/> policies add
/// stricter limits on top, so the stricter one wins. Health checks and the OpenAPI document opt out
/// (<c>DisableRateLimiting</c>), so probes never see 429. A rejection is 429 <c>too_many_requests</c> with <c>Retry-After</c>.
/// Behind CloudFront the client IP must come from forwarded headers (Design §20, M12).
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
            .Validate(o => o.AnonymousPerMinute > 0 && o.BookingPerMinute > 0 && o.DemoLoginPerMinute > 0, "Rate limits must be positive.")
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<ApiRateLimitOptions>>((options, limits) =>
        {
            var l = limits.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
                    ? RateLimitPartition.GetFixedWindowLimiter($"anonymous:{ClientKey(context)}", _ => Window(l.AnonymousPerMinute))
                    : RateLimitPartition.GetNoLimiter("signed-in"));
            options.AddPolicy(Booking, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(l.BookingPerMinute)));
            options.AddPolicy(DemoLogin, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(l.DemoLoginPerMinute)));
            options.OnRejected = OnRejectedAsync;
        });
        return services;
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
