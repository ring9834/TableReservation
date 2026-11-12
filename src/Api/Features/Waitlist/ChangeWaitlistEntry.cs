using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Waitlist;

/// <param name="TableId">The table that is ready (AC4.3).</param>
/// <param name="Version">The entry's <c>version</c>.</param>
internal sealed record NotifyWaitlistRequest(Guid TableId, uint Version);

/// <param name="Outcome"><c>walkedAway</c> or <c>removed</c> (AC3.7); a party is seated through <c>seatWalkIn</c>.</param>
/// <param name="Version">The entry's <c>version</c>.</param>
internal sealed record EndWaitlistRequest(WaitlistOutcome Outcome, uint Version);

/// <param name="BookingId">The walk-in booking, once seated.</param>
internal sealed record WaitlistEntryStateResponse(
    Guid Id, WaitlistStatus Status, uint Version, DateTime? NotifiedAt, Guid? NotifiedTableId, Guid? BookingId);

internal sealed record NotifyWaitlistEntry(VenueId VenueId, WaitlistEntryId EntryId, NotifyWaitlistRequest Request);

internal sealed record EndWaitlistEntry(VenueId VenueId, WaitlistEntryId EntryId, EndWaitlistRequest Request);

internal sealed class NotifyWaitlistRequestValidator : AbstractValidator<NotifyWaitlistRequest>
{
    public NotifyWaitlistRequestValidator()
    {
        RuleFor(x => x.TableId).NotEmpty().WithErrorCode("table_required");
        RuleFor(x => x.Version).GreaterThan(0u).WithErrorCode("version_required");
    }
}

internal sealed class EndWaitlistRequestValidator : AbstractValidator<EndWaitlistRequest>
{
    public EndWaitlistRequestValidator()
    {
        RuleFor(x => x.Outcome).Must(o => o is WaitlistOutcome.WalkedAway or WaitlistOutcome.Removed).WithErrorCode("outcome_invalid");
        RuleFor(x => x.Version).GreaterThan(0u).WithErrorCode("version_required");
    }
}

/// <summary>
/// §9.2 <c>NotifyWaitlistEntry</c> and <c>EndWaitlistEntry</c> (AC3.7, AC4.3, Design §14.2).
/// Notify: version check → table in the venue (404) and seating the party (422 <c>table_too_small</c>) → <c>Notify</c>
/// (re-notifying is allowed, §3.2). Status and event only; the "table ready" text comes with M7.
/// End: an entry already ended the same way returns its state (no-op); otherwise version check → <c>WalkAway</c> or
/// <c>Remove</c>. An entry that has already ended otherwise is 409 <c>invalid_transition</c>.
/// </summary>
internal sealed partial class ChangeWaitlistEntryHandler(
    ReservationsDbContext db,
    IUserContext user,
    TimeProvider clock,
    ILogger<ChangeWaitlistEntryHandler> logger)
    : ICommandHandler<NotifyWaitlistEntry, WaitlistEntryStateResponse>, ICommandHandler<EndWaitlistEntry, WaitlistEntryStateResponse>
{
    public async Task<Result<WaitlistEntryStateResponse>> Handle(NotifyWaitlistEntry command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = await db.WaitlistEntries.SingleOrDefaultAsync(w => w.Id == command.EntryId, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound();
        }

        if (entry.Version != command.Request.Version)
        {
            return Error.Conflict(ErrorCodes.VersionConflict);
        }

        var venue = await db.Venues.AsNoTracking().SingleAsync(v => v.Id == command.VenueId, cancellationToken);
        var tableId = new TableId(command.Request.TableId);
        var table = venue.FindTable(tableId);
        if (table is null)
        {
            return Error.NotFound();
        }

        if (!table.Fits(entry.PartySize))
        {
            return Error.Rule(ErrorCodes.TableTooSmall);
        }

        var notified = entry.Notify(tableId, clock.GetUtcNow());
        if (notified.IsFailure)
        {
            return notified.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogNotified(logger, entry.Id.Value, tableId.Value);
        return State(entry);
    }

    public async Task<Result<WaitlistEntryStateResponse>> Handle(EndWaitlistEntry command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = await db.WaitlistEntries.SingleOrDefaultAsync(w => w.Id == command.EntryId, cancellationToken);
        if (entry is null)
        {
            return Error.NotFound();
        }

        var target = command.Request.Outcome == WaitlistOutcome.WalkedAway ? WaitlistStatus.WalkedAway : WaitlistStatus.Removed;
        if (entry.Status != target)
        {
            if (entry.Version != command.Request.Version)
            {
                return Error.Conflict(ErrorCodes.VersionConflict);
            }

            var now = clock.GetUtcNow();
            var ended = target == WaitlistStatus.WalkedAway ? entry.WalkAway(now) : entry.Remove(user.Actor, now);
            if (ended.IsFailure)
            {
                return ended.Error!;
            }

            await db.SaveChangesAsync(cancellationToken);
            LogEnded(logger, entry.Id.Value, command.Request.Outcome);
        }

        return State(entry);
    }

    private static WaitlistEntryStateResponse State(WaitlistEntry entry) =>
        new(entry.Id.Value, entry.Status, entry.Version, entry.NotifiedAt?.UtcDateTime, entry.NotifiedTableId?.Value, entry.BookingId?.Value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waitlist entry {WaitlistEntryId} notified for {TableId}")]
    private static partial void LogNotified(ILogger logger, Guid waitlistEntryId, Guid tableId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waitlist entry {WaitlistEntryId} ended ({Outcome})")]
    private static partial void LogEnded(ILogger logger, Guid waitlistEntryId, WaitlistOutcome outcome);
}

/// <summary>
/// <c>POST /venues/{venueId}/waitlist/{id}/notify</c> with <c>{tableId, version}</c> and
/// <c>POST /venues/{venueId}/waitlist/{id}/end</c> with <c>{outcome, version}</c> (Host).
/// </summary>
internal sealed class ChangeWaitlistEntryEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/venues/{venueId:guid}/waitlist/{id:guid}/notify", NotifyAsync)
            .WithName("notifyWaitlistEntry")
            .WithTags("Waitlist")
            .RequireAuthorization(Policies.Host)
            .WithValidation<NotifyWaitlistRequest>()
            .Produces<WaitlistEntryStateResponse>()
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        api.MapPost("/venues/{venueId:guid}/waitlist/{id:guid}/end", EndAsync)
            .WithName("endWaitlistEntry")
            .WithTags("Waitlist")
            .RequireAuthorization(Policies.Host)
            .WithValidation<EndWaitlistRequest>()
            .Produces<WaitlistEntryStateResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> NotifyAsync(
        Guid venueId, Guid id, NotifyWaitlistRequest request, ChangeWaitlistEntryHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new NotifyWaitlistEntry(new VenueId(venueId), new WaitlistEntryId(id), request), cancellationToken)).ToHttp(TypedResults.Ok);

    private static async Task<IResult> EndAsync(
        Guid venueId, Guid id, EndWaitlistRequest request, ChangeWaitlistEntryHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new EndWaitlistEntry(new VenueId(venueId), new WaitlistEntryId(id), request), cancellationToken)).ToHttp(TypedResults.Ok);
}
