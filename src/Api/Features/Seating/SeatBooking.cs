using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Seating;

/// <param name="Version">The <c>version</c> the caller last read.</param>
/// <param name="TableIds">Omit to seat on the booking's own tables (one tap, AC3.3); otherwise the tables chosen by the host.</param>
internal sealed record SeatBookingRequest(uint Version, IReadOnlyList<Guid>? TableIds = null);

internal sealed record SeatBooking(VenueId VenueId, BookingId BookingId, SeatBookingRequest Request);

internal sealed class SeatBookingRequestValidator : AbstractValidator<SeatBookingRequest>
{
    public SeatBookingRequestValidator()
    {
        RuleFor(x => x.Version).GreaterThan(0u).WithErrorCode("version_required");
        RuleFor(x => x.TableIds!).NotEmpty().WithErrorCode("tables_required").When(x => x.TableIds is not null);
        RuleForEach(x => x.TableIds).NotEmpty().WithErrorCode("tables_required").When(x => x.TableIds is not null);
    }
}

/// <summary>
/// §9.2 <c>SeatBooking</c> (AC3.3, Design §14.2). Already seated → its current state (§3.1 no-op). Otherwise: version
/// check → tables in the venue (404) and seating the party (422 <c>table_too_small</c>) → one transaction: lock the
/// tables (§12, ascending ID) → re-check they are free for <c>[now, now + turn)</c>, ignoring the booking's own
/// assignment → <c>Seat</c> → save. A table taken by someone else, or by the next booking on it, is 409
/// <c>slot_taken</c> (Design §20). A table the booking keeps has its assignment moved in place (§14.2).
/// </summary>
internal sealed partial class SeatBookingHandler(
    ReservationsDbContext db,
    AvailabilityReader reader,
    IUserContext user,
    TimeProvider clock,
    ILogger<SeatBookingHandler> logger) : ICommandHandler<SeatBooking, BookingStateResponse>
{
    public async Task<Result<BookingStateResponse>> Handle(SeatBooking command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.Id == command.BookingId, cancellationToken);
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == command.VenueId, cancellationToken);
        if (booking is null || venue is null)
        {
            return Error.NotFound();
        }

        if (booking.Status == BookingStatus.Seated)
        {
            return State(booking);
        }

        if (booking.Version != command.Request.Version)
        {
            return Error.Conflict(ErrorCodes.VersionConflict);
        }

        if (booking.Status is not (BookingStatus.Booked or BookingStatus.Confirmed))
        {
            return Error.InvalidTransition;
        }

        List<TableId> tableIds = command.Request.TableIds is { } chosen
            ? [.. chosen.Distinct().Select(id => new TableId(id))]
            : [.. booking.Tables.Select(t => t.TableId)];
        var tables = tableIds.Select(venue.FindTable).ToList();
        if (tables.Count == 0 || tables.Any(t => t is null))
        {
            return Error.NotFound();
        }

        if (tables.Sum(t => t!.Seats) < booking.PartySize.Value)
        {
            return Error.Rule(ErrorCodes.TableTooSmall);
        }

        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TableLocks.LockAsync(db, tableIds, cancellationToken);
        var floor = await reader.LoadNowAsync(venue, now, cancellationToken);
        if (tableIds.Any(t => !SeatingRules.IsFreeNow(floor, t, booking.PartySize, except: booking.Id)))
        {
            return Error.Conflict(ErrorCodes.SlotTaken);
        }

        var seated = booking.Seat(tableIds, now, user.Actor);
        if (seated.IsFailure)
        {
            return seated.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogSeated(logger, booking.Id.Value);
        return State(booking);
    }

    private static BookingStateResponse State(Booking booking) =>
        new(booking.Id.Value, booking.Status, booking.Version, booking.CancellationRefund);

    [LoggerMessage(Level = LogLevel.Information, Message = "Booking {BookingId} seated")]
    private static partial void LogSeated(ILogger logger, Guid bookingId);
}

/// <summary><c>POST /venues/{venueId}/bookings/{id}/seat</c> with <c>{version, tableIds?}</c> (Host).</summary>
internal sealed class SeatBookingEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/bookings/{id:guid}/seat", HandleAsync)
            .WithName("seatBooking")
            .WithTags("Seating")
            .RequireAuthorization(Policies.Host)
            .WithValidation<SeatBookingRequest>()
            .Produces<BookingStateResponse>()
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> HandleAsync(
        Guid venueId, Guid id, SeatBookingRequest request, SeatBookingHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new SeatBooking(new VenueId(venueId), new BookingId(id), request), cancellationToken)).ToHttp(TypedResults.Ok);
}
