using System.Collections;
using System.Diagnostics;
using Reservation.Api.Auth;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Pipeline;

/// <summary>
/// The log scope of one request (ADR-004 "What every log line carries"): IDs only. Created by
/// <see cref="RequestLoggingMiddleware"/>; <see cref="RequestContextMiddleware"/> fills in venue, user and role after
/// sign-in. The JSON console formatter reads it at each log call, so later values show on later lines.
/// </summary>
internal sealed class RequestLogScope(Guid correlationId) : IReadOnlyList<KeyValuePair<string, object?>>
{
    public Guid CorrelationId { get; } = correlationId;

    public Guid? VenueId { get; set; }

    public string? UserId { get; set; }

    public string? Role { get; set; }

    public int Count => 4;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("CorrelationId", CorrelationId),
        1 => new("VenueId", VenueId),
        2 => new("UserId", UserId),
        3 => new("Role", Role),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"CorrelationId:{CorrelationId} VenueId:{VenueId} UserId:{UserId} Role:{Role}";
}

/// <summary>
/// Logging and tracing layer (ADR-004): takes the correlation ID from <c>X-Correlation-Id</c> (a GUID) or makes a
/// new UUID v7, echoes it in the response, opens the request's log scope and writes one line per request with the
/// method, the <b>route template</b> (never the raw path or query string), the status and the time taken.
/// Bodies, headers and cookies are never logged. Trace and span IDs come from the W3C <see cref="Activity"/>.
/// </summary>
internal sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        var correlationId = ReadCorrelationId(context) ?? Guid.CreateVersion7();
        requestContext.Set(correlationId, null, Actor.ForSystem());
        var scope = new RequestLogScope(correlationId);
        context.Features.Set(scope);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationHeader] = correlationId.ToString();
            return Task.CompletedTask;
        });

        var started = Stopwatch.GetTimestamp();
        var failed = false;
        using (logger.BeginScope(scope))
        {
            try
            {
                await next(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
                LogRequest(logger, context.Request.Method, RouteOf(context), status, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static Guid? ReadCorrelationId(HttpContext context) =>
        context.Request.Headers.TryGetValue(CorrelationHeader, out var values)
        && Guid.TryParse(values.ToString(), out var id) && id != Guid.Empty
            ? id
            : null;

    private static string RouteOf(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint endpoint ? endpoint.RoutePattern.RawText ?? "(unnamed)" : "(unmatched)";

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogRequest(ILogger logger, string method, string route, int statusCode, double elapsedMs);
}

/// <summary>
/// Runs after sign-in and the venue check (ADR-004 order). Sets the scoped venue context from the route's
/// <c>venueId</c> (public guest routes take it from the route; staff routes have passed the venue check), the request
/// context's actor (staff from the demo session, otherwise a guest), and the log scope's venue, user and role.
/// </summary>
internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, RequestContext requestContext, VenueContext venueContext, IUserContext user)
    {
        VenueId? venueId = context.GetRouteValue("venueId") is string raw && Guid.TryParse(raw, out var id) ? new VenueId(id) : null;
        venueContext.Set(venueId);
        requestContext.Set(requestContext.CorrelationId, null, user.Actor);

        if (context.Features.Get<RequestLogScope>() is { } scope)
        {
            scope.VenueId = venueId?.Value;
            scope.UserId = user.IsStaff ? user.Actor.Id : null;
            scope.Role = user.Actor.Role;
        }

        return next(context);
    }
}
