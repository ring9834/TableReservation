using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <summary>
/// The guest page's poll while paying (Design §10.1, §14, §8.4): states only, nothing personal. A paid deposit turns
/// <c>pendingPayment</c> into <c>booked</c>; a hold that ran out shows <c>cancelled</c> with <c>holdExpired</c>.
/// </summary>
/// <param name="DepositStatus">The deposit's state, when the booking takes one.</param>
/// <param name="CancelReason">Why the booking was cancelled, once it is.</param>
/// <param name="HoldExpiresAt">When an unpaid hold is released, while the booking waits for payment.</param>
internal sealed record BookingStatusResponse(
    Guid BookingId,
    BookingStatus Status,
    DepositStatus? DepositStatus,
    CancelReason? CancelReason,
    DateTime? HoldExpiresAt);

internal sealed record GetBookingStatus(VenueId VenueId, BookingId BookingId);

/// <summary>Reads the states straight into the response; the venue filter limits the lookup to the route's venue.</summary>
internal sealed class GetBookingStatusHandler(ReservationsDbContext db) : IQueryHandler<GetBookingStatus, BookingStatusResponse>
{
    public async Task<Result<BookingStatusResponse>> Handle(GetBookingStatus query, CancellationToken cancellationToken)
    {
        var status = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == query.BookingId && b.VenueId == query.VenueId)
            .Select(b => new BookingStatusResponse(
                b.Id.Value,
                b.Status,
                b.Deposit == null ? null : b.Deposit.Status,
                b.CancellationReason,
                b.Status == BookingStatus.PendingPayment && b.HoldExpiresAt != null ? b.HoldExpiresAt.Value.UtcDateTime : null))
            .SingleOrDefaultAsync(cancellationToken);
        return status is null ? Error.NotFound() : status;
    }
}

/// <summary>
/// <c>GET /venues/{venueId}/bookings/{id}/status</c> (guest, public). Under the anonymous limit (60 a minute per IP), which
/// a poll every 2 seconds stays inside.
/// </summary>
internal sealed class GetBookingStatusEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/bookings/{id:guid}/status", HandleAsync)
            .WithName("getBookingStatus")
            .WithTags("Bookings")
            .AllowAnonymous()
            .Produces<BookingStatusResponse>()
            .ProducesApiProblem(StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> HandleAsync(Guid venueId, Guid id, GetBookingStatusHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetBookingStatus(new VenueId(venueId), new BookingId(id)), cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
