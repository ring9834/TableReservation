using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Reservation.Domain.Common;

namespace Reservation.Api.Common;

/// <summary>Error codes the HTTP layer adds to the domain's <see cref="ErrorCodes"/> (ADR-004 table).</summary>
internal static class ProblemCodes
{
    public const string NotSignedIn = "not_signed_in";
    public const string TooManyRequests = "too_many_requests";
    public const string Unexpected = "unexpected";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string PaymentProviderError = "payment_provider_error";
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    // Stripe webhook (M8a, Design §8.4): a bad or missing Stripe-Signature (401), a body over 64 KB (413).
    public const string InvalidSignature = "invalid_signature";
    public const string PayloadTooLarge = "payload_too_large";
}

/// <summary>
/// The one place that turns an error code into Problem Details (RFC 9457, ADR-004): <c>type</c>, English <c>title</c>
/// (for logs only), <c>status</c>, stable <c>code</c>, <c>traceId</c> and <c>errors</c> (field → rule codes, 400 only).
/// </summary>
internal static class ApiProblems
{
    /// <summary>Placeholder until the product domain is chosen (ADR-004).</summary>
    public const string TypeBase = "https://errors.example.app/";

    public const string ContentType = "application/problem+json";

    private static readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal)
    {
        [ErrorCodes.ValidationFailed] = "One or more fields are invalid",
        [ProblemCodes.NotSignedIn] = "Sign-in required",
        [ErrorCodes.Forbidden] = "Not allowed",
        [ErrorCodes.NotFound] = "Not found",
        [ProblemCodes.MethodNotAllowed] = "Method not allowed",
        [ErrorCodes.SlotTaken] = "The table is no longer free",
        [ErrorCodes.VersionConflict] = "The record was changed by someone else",
        [ErrorCodes.HoldExpired] = "The payment hold has expired",
        [ErrorCodes.InvalidTransition] = "The record is not in a state that allows this action",
        [ProblemCodes.TooManyRequests] = "Too many requests",
        [ProblemCodes.PaymentProviderError] = "The payment provider failed",
        [ProblemCodes.InvalidSignature] = "The signature is not valid",
        [ProblemCodes.PayloadTooLarge] = "The request body is too large",
        [ProblemCodes.Unexpected] = "An unexpected error occurred",
    };

    /// <summary>ADR-004 error table: the HTTP status for each kind of expected failure.</summary>
    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Rule => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.External => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>The code for a response that has only a status (framework 401/403/404/405/429, empty status pages).</summary>
    public static string CodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.ValidationFailed,
        StatusCodes.Status401Unauthorized => ProblemCodes.NotSignedIn,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => ProblemCodes.MethodNotAllowed,
        StatusCodes.Status422UnprocessableEntity => ErrorCodes.RuleBroken,
        StatusCodes.Status429TooManyRequests => ProblemCodes.TooManyRequests,
        StatusCodes.Status502BadGateway => ProblemCodes.PaymentProviderError,
        >= 500 => ProblemCodes.Unexpected,
        _ => $"http_{status}",
    };

    public static string TypeFor(string code) => TypeBase + code.Replace('_', '-');

    public static string TitleFor(string code, int status) =>
        _titles.TryGetValue(code, out var title)
            ? title
            : status == StatusCodes.Status422UnprocessableEntity ? "A business rule was broken" : "The request failed";

    public static ProblemDetails Create(int status, string code, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails { Status = status, Type = TypeFor(code), Title = TitleFor(code, status) };
        problem.Extensions["code"] = code;
        problem.Extensions["errors"] = errors;
        return problem;
    }

    public static IResult Result(int status, string code, IReadOnlyDictionary<string, string[]>? errors = null) =>
        TypedResults.Problem(Create(status, code, errors));

    /// <summary>
    /// Runs for every Problem Details response, including the framework's own (status pages, auth): fills in
    /// <c>code</c> when missing, sets <c>traceId</c> and <c>errors</c>, and removes anything that could leak internals.
    /// </summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var status = problem.Status ?? context.HttpContext.Response.StatusCode;
        problem.Status = status;
        if (!problem.Extensions.TryGetValue("code", out var existing) || existing is not string)
        {
            var code = CodeFor(status);
            problem.Extensions["code"] = code;
            problem.Type = TypeFor(code);
            problem.Title = TitleFor(code, status);
        }

        problem.Detail = null;
        problem.Instance = null;
        problem.Extensions.Remove("exception");
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        problem.Extensions.TryAdd("errors", null);
    }
}

/// <summary>The Problem Details body as documented in OpenAPI, so the generated client knows <c>code</c> and <c>errors</c>.</summary>
/// <param name="Type">URI of the error type.</param>
/// <param name="Title">English, for logs only; screens translate <paramref name="Code"/>.</param>
/// <param name="Status">HTTP status.</param>
/// <param name="Code">Stable error code the front end switches on (ADR-004).</param>
/// <param name="TraceId">W3C trace ID for support.</param>
/// <param name="Errors">Field errors for 400: field name → rule codes.</param>
internal sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string? TraceId,
    IReadOnlyDictionary<string, string[]>? Errors);

internal static class ApiProblemEndpointExtensions
{
    /// <summary>Documents Problem Details responses for the given statuses.</summary>
    public static RouteHandlerBuilder ProducesApiProblem(this RouteHandlerBuilder builder, params int[] statuses)
    {
        foreach (var status in statuses)
        {
            builder.Produces<ApiProblem>(status, ApiProblems.ContentType);
        }

        return builder;
    }
}
