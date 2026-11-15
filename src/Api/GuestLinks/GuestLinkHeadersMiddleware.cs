namespace Reservation.Api.GuestLinks;

/// <summary>
/// Every response under <c>/api/v1/guest/</c>, errors included, says <c>Referrer-Policy: no-referrer</c> and
/// <c>Cache-Control: no-store</c> (Design §7.3), so a guest link's token is never passed on in a Referer header or kept
/// in a shared cache. Set when the response starts, so the exception handler's cleared response still gets them.
/// </summary>
internal sealed class GuestLinkHeadersMiddleware(RequestDelegate next)
{
    public const string PathPrefix = "/api/v1/guest";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
        }

        return next(context);
    }
}
