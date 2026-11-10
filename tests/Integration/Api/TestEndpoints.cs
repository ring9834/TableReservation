using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Domain.Common;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// Test-only routes under <c>/api/v1/test</c>, added through DI by the tests that need them; never part of the API
/// or its OpenAPI document. They reach responses no M4a route can: a Presenter-only route, <c>hold_expired</c>, and an
/// unexpected exception.
/// </summary>
internal sealed class TestEndpoints : IEndpoint
{
    public const string Secret = "secret-internal-detail";

    public static void Add(IServiceCollection services) => services.AddSingleton<IEndpoint, TestEndpoints>();

    public void Map(IEndpointRouteBuilder api)
    {
        var test = api.MapGroup("/test");
        test.MapGet("/venues/{venueId:guid}/presenter-only", () => TypedResults.NoContent()).RequireAuthorization(Policies.Presenter);
        test.MapGet("/hold-expired", () => ResultToHttp.ToProblem(Error.Conflict(ErrorCodes.HoldExpired))).AllowAnonymous();
        test.MapGet("/boom", Boom).AllowAnonymous();
    }

    private static IResult Boom() => throw new InvalidOperationException(Secret);
}
