using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservation.Api.Common;
using Reservation.Domain.Common;

namespace Reservation.Api.Pipeline;

/// <summary>
/// The central exception handler (ADR-004): the only place an exception becomes a response, logged once.
/// Known infrastructure failures are mapped to their stable codes: a stale row version → 409
/// <c>version_conflict</c>, the exclusion constraint (<c>23P01</c>) and a deadlock or serialization failure
/// (<c>40P01</c>, <c>40001</c>) → 409 <c>slot_taken</c>, a body or parameter
/// that can't be read → 400 <c>validation_failed</c>. A request the client abandoned gets no response and no error log.
/// Anything else is 500 <c>unexpected</c>, with details only in the log.
/// </summary>
internal sealed partial class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    /// <summary>Not an HTTP standard; nginx's "client closed request", used only so the request log shows it.</summary>
    public const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The request's log scope has closed by now; reopen it so this line carries the correlation ID.
        using var scope = httpContext.Features.Get<RequestLogScope>() is { } requestScope ? logger.BeginScope(requestScope) : null;

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var (status, code) = Classify(exception);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnexpected(logger, exception);
        }
        else
        {
            LogMapped(logger, exception.GetType().Name, code);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = ApiProblems.Create(status, code),
        });
    }

    internal static (int Status, string Code) Classify(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.VersionConflict),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } } =>
            (StatusCodes.Status409Conflict, ErrorCodes.SlotTaken),
        _ when IsLostRace(exception) => (StatusCodes.Status409Conflict, ErrorCodes.SlotTaken),
        BadHttpRequestException bad => (bad.StatusCode, ApiProblems.CodeFor(bad.StatusCode)),
        _ => (StatusCodes.Status500InternalServerError, ProblemCodes.Unexpected),
    };

    /// <summary>
    /// Safety net (§12): a deadlock (<c>40P01</c>) or serialization failure (<c>40001</c>) means PostgreSQL rolled
    /// back one of two competing transactions. The table locks in <c>BookingCreator</c> should prevent it; if it
    /// still happens, the loser gets 409 <c>slot_taken</c>, not 500.
    /// </summary>
    private static bool IsLostRace(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{ExceptionType} mapped to {Code}")]
    private static partial void LogMapped(ILogger logger, string exceptionType, string code);
}
