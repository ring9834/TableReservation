using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <summary>
/// The guest page's poll while paying (Design §10.1, §14, §8.4): states only, nothing personal. A paid deposit turns
/// <c>pendingPayment</c> into <c>booked</c>; a hold that ran out shows <c>cancelled</c> with <c>holdExpired</c>.
/// </summary>
/// <param name="DepositStatus">The deposit's state, when the booking takes one.</param>
/// <param name="CancelReason">Why the booking was cancelled, once it is.</param>
/// <param name="HoldExpiresAt">When an unpaid hold is released, while the booking waits for payment.</param>
/// <param name="HoldSecondsRemaining">
/// Whole seconds left on the hold (rounded up, never negative), worked out on the server's infrastructure clock while
/// the booking waits for payment (M8b): the guest's countdown never depends on the guest's own clock.
/// </param>
internal sealed record BookingStatusResponse(
    Guid BookingId,
    BookingStatus Status,
    DepositStatus? DepositStatus,
    CancelReason? CancelReason,
    DateTime? HoldExpiresAt,
    int? HoldSecondsRemaining);

internal sealed record GetBookingStatus(VenueId VenueId, BookingId BookingId);

/// <summary>Reads the states straight into the response; the venue filter limits the lookup to the route's venue.</summary>
internal sealed class GetBookingStatusHandler(
    ReservationsDbContext db,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock) : IQueryHandler<GetBookingStatus, BookingStatusResponse>
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
                b.Status == BookingStatus.PendingPayment && b.HoldExpiresAt != null ? b.HoldExpiresAt.Value.UtcDateTime : null,
                (int?)null))
            .SingleOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return Error.NotFound();
        }

        return status.HoldExpiresAt is { } expiresAt
            ? status with { HoldSecondsRemaining = SecondsLeft(new DateTimeOffset(expiresAt, TimeSpan.Zero), infrastructureClock.GetUtcNow()) }
            : status;
    }

    /// <summary>Whole seconds from <paramref name="now"/> to <paramref name="expiresAt"/>, rounded up, at least 0.</summary>
    public static int SecondsLeft(DateTimeOffset expiresAt, DateTimeOffset now) =>
        (int)Math.Max(0, Math.Ceiling((expiresAt - now).TotalSeconds));
}

/// <summary>
/// <c>GET /venues/{venueId}/bookings/{id}/status</c> (guest, public). Limited per booking (<c>RateLimits:BookingStatusPerMinute</c>,
/// 60 a minute; a poll every 2 seconds is 30), not in the per-IP bucket, so guests sharing venue Wi-Fi don't use up each
/// other's allowance (Design §18.10).
/// </summary>
internal sealed class GetBookingStatusEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/bookings/{id:guid}/status", HandleAsync)
            .WithName("getBookingStatus")
            .WithTags("Bookings")
            .AllowAnonymous()
            .WithAnonymousBucket(AnonymousBucket.BookingStatus)
            .Produces<BookingStatusResponse>()
            .ProducesApiProblem(StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> HandleAsync(Guid venueId, Guid id, GetBookingStatusHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetBookingStatus(new VenueId(venueId), new BookingId(id)), cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
