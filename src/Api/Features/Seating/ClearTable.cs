using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Seating;

/// <param name="BookingId">The booking seated at the table (the floor plan's <c>current.bookingId</c>).</param>
/// <param name="Version">That booking's <c>version</c> (the floor plan's <c>current.version</c>).</param>
internal sealed record ClearTableRequest(Guid BookingId, uint Version);

internal sealed record ClearTable(VenueId VenueId, TableId TableId, ClearTableRequest Request);

internal sealed class ClearTableRequestValidator : AbstractValidator<ClearTableRequest>
{
    public ClearTableRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty().WithErrorCode("booking_required");
        RuleFor(x => x.Version).GreaterThan(0u).WithErrorCode("version_required");
    }
}

/// <summary>
/// §9.2 <c>ClearTable</c> (AC4.1, Design §14.2): <c>Complete(ClearedByHost)</c> on the booking seated at the table,
/// raising <c>BookingCompleted</c> and <c>TableCleared</c>. The booking must hold the table (else 404). Already completed
/// → its current state, so a retried tap is safe; otherwise a stale version is 409 <c>version_conflict</c> and a booking
/// that isn't seated is 409 <c>invalid_transition</c>. "Bill paid" through the mock POS comes in M9.
/// </summary>
internal sealed partial class ClearTableHandler(ReservationsDbContext db, TimeProvider clock, ILogger<ClearTableHandler> logger)
    : ICommandHandler<ClearTable, BookingStateResponse>
{
    public async Task<Result<BookingStateResponse>> Handle(ClearTable command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var bookingId = new BookingId(command.Request.BookingId);
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null || booking.Tables.All(t => t.TableId != command.TableId))
        {
            return Error.NotFound();
        }

        if (booking.Status != BookingStatus.Completed)
        {
            if (booking.Version != command.Request.Version)
            {
                return Error.Conflict(ErrorCodes.VersionConflict);
            }

            var completed = booking.Complete(CompletionSource.ClearedByHost, null, clock.GetUtcNow());
            if (completed.IsFailure)
            {
                return completed.Error!;
            }

            await db.SaveChangesAsync(cancellationToken);
            LogCleared(logger, command.TableId.Value, booking.Id.Value);
        }

        return new BookingStateResponse(booking.Id.Value, booking.Status, booking.Version, booking.CancellationRefund);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Table {TableId} cleared (booking {BookingId})")]
    private static partial void LogCleared(ILogger logger, Guid tableId, Guid bookingId);
}

/// <summary><c>POST /venues/{venueId}/tables/{tableId}/clear</c> with <c>{bookingId, version}</c> (Host).</summary>
internal sealed class ClearTableEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/tables/{tableId:guid}/clear", HandleAsync)
            .WithName("clearTable")
            .WithTags("Seating")
            .RequireAuthorization(Policies.Host)
            .WithValidation<ClearTableRequest>()
            .Produces<BookingStateResponse>()
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(
        Guid venueId, Guid tableId, ClearTableRequest request, ClearTableHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ClearTable(new VenueId(venueId), new TableId(tableId), request), cancellationToken)).ToHttp(TypedResults.Ok);
}
