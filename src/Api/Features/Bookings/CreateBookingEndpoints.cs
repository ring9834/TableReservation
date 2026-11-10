using Microsoft.AspNetCore.Mvc;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;

namespace Reservation.Api.Features.Bookings;

/// <summary>
/// <c>POST /venues/{venueId}/bookings</c>: online booking by a guest (public, 10 a minute per IP). A party of 6 or
/// more is created as <c>pendingPayment</c> with the deposit due; capture arrives with payments (M8).
/// </summary>
internal sealed class CreateOnlineBookingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/bookings", HandleAsync)
            .WithName("createOnlineBooking")
            .WithTags("Bookings")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimits.Booking)
            .WithValidation<CreateBookingRequest>()
            .Produces<CreateBookingResponse>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status429TooManyRequests);

    private static Task<IResult> HandleAsync(
        Guid venueId,
        CreateBookingRequest request,
        [FromHeader(Name = IdempotencyKey.Header)] string? idempotencyKey,
        BookingCreator creator,
        CancellationToken cancellationToken) =>
        CreateBookingEndpoints.CreateAsync(venueId, BookingChannel.Online, request, idempotencyKey, creator, cancellationToken);
}

/// <summary>
/// <c>POST /venues/{venueId}/phone-bookings</c>: a host enters a booking taken by phone (Design §14.1). Party 1–20,
/// never a deposit (§8.2), same slot and pacing rules, table chosen by BR-05.
/// </summary>
internal sealed class CreatePhoneBookingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/phone-bookings", HandleAsync)
            .WithName("createPhoneBooking")
            .WithTags("Bookings")
            .RequireAuthorization(Policies.Host)
            .WithValidation<CreateBookingRequest>()
            .Produces<CreateBookingResponse>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

    private static Task<IResult> HandleAsync(
        Guid venueId,
        CreateBookingRequest request,
        [FromHeader(Name = IdempotencyKey.Header)] string? idempotencyKey,
        BookingCreator creator,
        CancellationToken cancellationToken) =>
        CreateBookingEndpoints.CreateAsync(venueId, BookingChannel.Phone, request, idempotencyKey, creator, cancellationToken);
}

internal static class CreateBookingEndpoints
{
    public static async Task<IResult> CreateAsync(
        Guid venueId, BookingChannel channel, CreateBookingRequest request, string? idempotencyKey, BookingCreator creator,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.IsValid(idempotencyKey))
        {
            return ResultToHttp.ToProblem(IdempotencyKey.Invalid);
        }

        var result = await creator.CreateAsync(new VenueId(venueId), channel, request, idempotencyKey, cancellationToken);
        return result.ToHttp(response => TypedResults.Created((string?)null, response));
    }
}
