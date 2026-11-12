using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Seating;

/// <param name="PartySize">1–20.</param>
/// <param name="TableId">The suggested table or another free one (AC3.1, AC3.2).</param>
/// <param name="Name">Optional (AC3.1), 1–80 characters; the booking's contact name. Ignored when seating from the waitlist.</param>
/// <param name="WaitlistEntryId">Seat a waiting party (AC4.5): the entry ends as seated in the same transaction.</param>
/// <param name="WaitlistEntryVersion">The entry's <c>version</c>; required with <c>waitlistEntryId</c>.</param>
internal sealed record SeatWalkInRequest(
    int PartySize,
    Guid TableId,
    string? Name = null,
    Guid? WaitlistEntryId = null,
    uint? WaitlistEntryVersion = null);

/// <param name="GuestName">The contact name, if any.</param>
/// <param name="WaitlistEntryId">Set when seated from the waitlist.</param>
internal sealed record SeatWalkInResponse(
    Guid BookingId,
    string Reference,
    BookingStatus Status,
    uint Version,
    DateTime StartsAt,
    DateTime EndsAt,
    int PartySize,
    IReadOnlyList<Guid> TableIds,
    string? GuestName,
    Guid? WaitlistEntryId);

internal sealed record SeatWalkIn(VenueId VenueId, SeatWalkInRequest Request, string? IdempotencyKey);

internal sealed class SeatWalkInRequestValidator : AbstractValidator<SeatWalkInRequest>
{
    public SeatWalkInRequestValidator()
    {
        RuleFor(x => x.PartySize).InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
        RuleFor(x => x.TableId).NotEmpty().WithErrorCode("table_required");
        RuleFor(x => x.Name!).Cascade(CascadeMode.Stop)
            .MaximumLength(PersonName.MaxLength).WithErrorCode("name_length")
            .Must(name => PersonName.Create(name).IsSuccess).WithErrorCode("name_length")
            .When(x => !string.IsNullOrWhiteSpace(x.Name));
        RuleFor(x => x.WaitlistEntryVersion).NotNull().WithErrorCode("waitlist_entry_version_required")
            .GreaterThan(0u).WithErrorCode("waitlist_entry_version_required")
            .When(x => x.WaitlistEntryId is not null);
    }
}

/// <summary>
/// §9.2 <c>SeatWalkIn</c> (Design §10.2, §10.3, §14.2), one transaction: claim the idempotency key → (from the waitlist:
/// load the entry and check its version) → lock the table (§12, ascending ID) → re-check that it is free for
/// <c>[now, now + turn)</c> → <c>Booking.CreateWalkIn</c> (+ <c>WaitlistEntry.MarkSeated</c>) → save → commit. Walk-ins
/// don't count towards pacing, so there is no slot lock. A taken table is 409 <c>slot_taken</c>, whether the re-check
/// or the exclusion constraint (<c>23P01</c>) finds it.
/// </summary>
internal sealed partial class SeatWalkInHandler(
    ReservationsDbContext db,
    AvailabilityReader reader,
    IdempotentRequests idempotency,
    TimeProvider clock,
    ILogger<SeatWalkInHandler> logger) : ICommandHandler<SeatWalkIn, SeatWalkInResponse>
{
    private const string Operation = "seat-walk-in";

    public async Task<Result<SeatWalkInResponse>> Handle(SeatWalkIn command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        var now = clock.GetUtcNow();
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == command.VenueId, cancellationToken);
        var tableId = new TableId(request.TableId);
        var table = venue?.FindTable(tableId);
        if (venue is null || table is null)
        {
            return Error.NotFound();
        }

        var partySize = PartySize.Of(request.PartySize);
        if (!table.Fits(partySize))
        {
            return Error.Rule(ErrorCodes.TableTooSmall);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replay = await idempotency.TryReplayAsync<SeatWalkInResponse>(command.VenueId, Operation, command.IdempotencyKey, request, cancellationToken);
        if (replay is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        WaitlistEntry? entry = null;
        if (request.WaitlistEntryId is { } entryId)
        {
            entry = await db.WaitlistEntries.SingleOrDefaultAsync(w => w.Id == new WaitlistEntryId(entryId), cancellationToken);
            if (entry is null)
            {
                return Error.NotFound();
            }

            if (entry.Version != request.WaitlistEntryVersion)
            {
                return Error.Conflict(ErrorCodes.VersionConflict);
            }

            if (!entry.IsActive)
            {
                return Error.InvalidTransition;
            }
        }

        await TableLocks.LockAsync(db, [tableId], cancellationToken);
        var floor = await reader.LoadNowAsync(venue, now, cancellationToken);
        if (!SeatingRules.IsFreeNow(floor, tableId, partySize))
        {
            return Error.Conflict(ErrorCodes.SlotTaken);
        }

        var contactName = entry?.ContactName
            ?? (string.IsNullOrWhiteSpace(request.Name) ? null : PersonName.Create(request.Name).Value);
        var created = Booking.CreateWalkIn(
            venue, entry?.GuestId, contactName, await BookingReferences.NewAsync(db, cancellationToken), partySize, [tableId],
            entry?.Id, now);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var booking = created.Value;
        db.Bookings.Add(booking);
        if (entry is not null)
        {
            var seated = entry.MarkSeated(booking.Id, now);
            if (seated.IsFailure)
            {
                return seated.Error!;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var response = new SeatWalkInResponse(
            booking.Id.Value,
            booking.Reference.Value,
            booking.Status,
            booking.Version,
            booking.Period.Start.UtcDateTime,
            booking.Period.End.UtcDateTime,
            booking.PartySize.Value,
            [.. booking.Tables.Select(t => t.TableId.Value)],
            booking.ContactName?.Value,
            entry?.Id.Value);
        await idempotency.CompleteAsync(command.VenueId, Operation, command.IdempotencyKey, response, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogSeated(logger, booking.Id.Value, tableId.Value, entry?.Id.Value);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Walk-in {BookingId} seated at {TableId} (waitlist entry {WaitlistEntryId})")]
    private static partial void LogSeated(ILogger logger, Guid bookingId, Guid tableId, Guid? waitlistEntryId);
}

/// <summary><c>POST /venues/{venueId}/walk-ins</c> (Host), optional <c>Idempotency-Key</c>.</summary>
internal sealed class SeatWalkInEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/walk-ins", HandleAsync)
            .WithName("seatWalkIn")
            .WithTags("Seating")
            .RequireAuthorization(Policies.Host)
            .WithValidation<SeatWalkInRequest>()
            .Produces<SeatWalkInResponse>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> HandleAsync(
        Guid venueId,
        SeatWalkInRequest request,
        [FromHeader(Name = IdempotencyKey.Header)] string? idempotencyKey,
        SeatWalkInHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.IsValid(idempotencyKey))
        {
            return ResultToHttp.ToProblem(IdempotencyKey.Invalid);
        }

        var result = await handler.Handle(new SeatWalkIn(new VenueId(venueId), request, idempotencyKey), cancellationToken);
        return result.ToHttp(response => TypedResults.Created((string?)null, response));
    }
}
