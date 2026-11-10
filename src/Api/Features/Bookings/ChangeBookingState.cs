using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <param name="Version">The <c>version</c> the caller last read (ADR-004 concurrency).</param>
internal sealed record BookingVersionRequest(uint Version);

/// <param name="Version">The new row version.</param>
/// <param name="RefundDecision">Set once the booking is cancelled (§8.3).</param>
internal sealed record BookingStateResponse(Guid BookingId, BookingStatus Status, uint Version, RefundDecision? RefundDecision);

internal sealed class BookingVersionRequestValidator : AbstractValidator<BookingVersionRequest>
{
    public BookingVersionRequestValidator()
    {
        RuleFor(x => x.Version).GreaterThan(0u).WithErrorCode("version_required");
    }
}

/// <param name="VenueId">From the route; the venue filter already limits the lookup to it.</param>
internal sealed record ConfirmBooking(VenueId VenueId, BookingId BookingId, uint Version);

/// <param name="VenueId">From the route; the venue filter already limits the lookup to it.</param>
internal sealed record CancelBooking(VenueId VenueId, BookingId BookingId, uint Version);

/// <summary>
/// Host confirm and cancel (Design §9.2, §14.1). A booking already in the target state returns its current state
/// whatever version was sent, so a retried tap is safe (§3.1 no-op). Otherwise a <c>version</c> that differs from
/// the stored one is 409 <c>version_conflict</c>; a change between this read and the save is caught by the xmin
/// check (also <c>version_conflict</c>). A transition the state machine refuses is 409 <c>invalid_transition</c>.
/// </summary>
internal sealed class ChangeBookingStateHandler(ReservationsDbContext db, IUserContext user, TimeProvider clock)
    : ICommandHandler<ConfirmBooking, BookingStateResponse>, ICommandHandler<CancelBooking, BookingStateResponse>
{
    public Task<Result<BookingStateResponse>> Handle(ConfirmBooking command, CancellationToken cancellationToken) =>
        ChangeAsync(
            command.BookingId, command.Version, BookingStatus.Confirmed,
            (booking, now) => booking.Confirm(user.Actor, now), cancellationToken);

    public Task<Result<BookingStateResponse>> Handle(CancelBooking command, CancellationToken cancellationToken) =>
        ChangeAsync(
            command.BookingId, command.Version, BookingStatus.Cancelled,
            (booking, now) => booking.Cancel(user.Actor, CancelReason.HostRequest, now), cancellationToken);

    private async Task<Result<BookingStateResponse>> ChangeAsync(
        BookingId bookingId, uint version, BookingStatus target, Func<Booking, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return Error.NotFound();
        }

        if (booking.Status != target)
        {
            if (booking.Version != version)
            {
                return Error.Conflict(ErrorCodes.VersionConflict);
            }

            var result = change(booking, clock.GetUtcNow());
            if (result.IsFailure)
            {
                return result.Error!;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new BookingStateResponse(booking.Id.Value, booking.Status, booking.Version, booking.CancellationRefund);
    }
}

/// <summary><c>POST …/bookings/{id}/confirm</c> and <c>POST …/bookings/{id}/cancel</c> with <c>{version}</c> (Host).</summary>
internal sealed class ChangeBookingStateEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/venues/{venueId:guid}/bookings/{id:guid}/confirm", ConfirmAsync)
            .WithName("confirmBooking")
            .WithTags("Bookings")
            .RequireAuthorization(Policies.Host)
            .WithValidation<BookingVersionRequest>()
            .Produces<BookingStateResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        api.MapPost("/venues/{venueId:guid}/bookings/{id:guid}/cancel", CancelAsync)
            .WithName("cancelBooking")
            .WithTags("Bookings")
            .RequireAuthorization(Policies.Host)
            .WithValidation<BookingVersionRequest>()
            .Produces<BookingStateResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ConfirmAsync(
        Guid venueId, Guid id, BookingVersionRequest request, ChangeBookingStateHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ConfirmBooking(new VenueId(venueId), new BookingId(id), request.Version), cancellationToken)).ToHttp(TypedResults.Ok);

    private static async Task<IResult> CancelAsync(
        Guid venueId, Guid id, BookingVersionRequest request, ChangeBookingStateHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new CancelBooking(new VenueId(venueId), new BookingId(id), request.Version), cancellationToken)).ToHttp(TypedResults.Ok);
}
