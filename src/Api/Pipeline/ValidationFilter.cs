using System.Text.Json;
using FluentValidation;
using Reservation.Api.Common;
using Reservation.Domain.Common;

namespace Reservation.Api.Pipeline;

/// <summary>
/// Runs the request's FluentValidation validator before the handler (ADR-004, Design §9.1). A failure returns
/// 400 <c>validation_failed</c> with <c>errors</c> mapping each camelCase field to its rule codes; the handler never runs.
/// </summary>
internal sealed class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return await next(context);
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(e => FieldName(e.PropertyName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorCode).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        return ApiProblems.Result(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, errors);
    }

    private static string FieldName(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}

internal static class ValidationFilterExtensions
{
    /// <summary>Validates the <typeparamref name="TRequest"/> argument and documents the 400 response.</summary>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>().ProducesApiProblem(StatusCodes.Status400BadRequest);
}
