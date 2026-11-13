using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Waitlist;

/// <param name="Name">1–80 characters; the entry's contact name. A known mobile keeps its stored guest name.</param>
/// <param name="Mobile">NZ mobile in any common format (BR-12); the "table ready" text goes to it (M7).</param>
/// <param name="PartySize">1–20.</param>
/// <param name="QuotedWaitMinutes">0–240: the quote from <c>getQuotedWait</c>, as adjusted by the host (AC3.5).</param>
internal sealed record JoinWaitlistRequest(string Name, string Mobile, int PartySize, int QuotedWaitMinutes)
{
    /// <summary>
    /// The language of this entry's messages (<c>en</c> when omitted), stored on the entry (M7a). A new guest also gets it as
    /// the profile language; a known guest's profile keeps its stored one. An init property, not a parameter default, so the
    /// shared <c>Language</c> schema in OpenAPI gets no default.
    /// </summary>
    public Language Language { get; init; } = Language.En;
}

internal sealed record JoinWaitlist(VenueId VenueId, JoinWaitlistRequest Request, string? IdempotencyKey);

internal sealed class JoinWaitlistRequestValidator : AbstractValidator<JoinWaitlistRequest>
{
    public JoinWaitlistRequestValidator()
    {
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("name_length")
            .MaximumLength(PersonName.MaxLength).WithErrorCode("name_length")
            .Must(name => PersonName.Create(name).IsSuccess).WithErrorCode("name_length");
        RuleFor(x => x.Mobile).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode("mobile_invalid").Custom((mobile, context) =>
        {
            var parsed = PhoneNumber.Create(mobile);
            if (parsed.IsFailure)
            {
                context.AddFailure(new ValidationFailure(nameof(JoinWaitlistRequest.Mobile), "Invalid mobile number.")
                {
                    ErrorCode = parsed.Error!.Fields?.Values.SelectMany(v => v).FirstOrDefault() ?? "mobile_invalid",
                });
            }
        });
        RuleFor(x => x.PartySize).InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
        RuleFor(x => x.QuotedWaitMinutes).InclusiveBetween(0, WaitlistEntry.MaxQuotedWaitMinutes).WithErrorCode("quoted_wait_out_of_range");
        RuleFor(x => x.Language).IsInEnum().WithErrorCode("language_invalid");
    }
}

/// <summary>
/// §9.2 <c>JoinWaitlist</c> (AC3.4, AC3.5, Design §14.2), one transaction: claim the idempotency key → find the guest by
/// mobile or create one (a known guest keeps its stored name and language) → <c>WaitlistEntry.Join</c> with
/// the name given now → save → commit. Two first joins for one new mobile at once: the second is retried once.
/// </summary>
internal sealed partial class JoinWaitlistHandler(
    ReservationsDbContext db,
    IdempotentRequests idempotency,
    TimeProvider clock,
    ILogger<JoinWaitlistHandler> logger) : ICommandHandler<JoinWaitlist, WaitlistItem>
{
    private const string Operation = "join-waitlist";

    public async Task<Result<WaitlistItem>> Handle(JoinWaitlist command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await JoinOnceAsync(command, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt == 1 && IsGuestMobileTaken(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Result<WaitlistItem>> JoinOnceAsync(JoinWaitlist command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var now = clock.GetUtcNow();
        if (!await db.Venues.AnyAsync(v => v.Id == command.VenueId, cancellationToken))
        {
            return Error.NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var replay = await idempotency.TryReplayAsync<WaitlistItem>(command.VenueId, Operation, command.IdempotencyKey, request, cancellationToken);
        if (replay is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return replay;
        }

        var name = PersonName.Create(request.Name).Value;
        var mobile = PhoneNumber.Create(request.Mobile).Value;
        var guest = await db.Guests.SingleOrDefaultAsync(g => g.Mobile == mobile, cancellationToken);
        if (guest is null)
        {
            guest = Guest.Create(command.VenueId, name, mobile, email: null, request.Language, allergyNotes: null, now);
            db.Guests.Add(guest);
        }

        var joined = WaitlistEntry.Join(command.VenueId, guest.Id, name, PartySize.Of(request.PartySize), request.QuotedWaitMinutes, now, request.Language);
        if (joined.IsFailure)
        {
            return joined.Error!;
        }

        var entry = joined.Value;
        db.WaitlistEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        var item = new WaitlistItem(
            entry.Id.Value, entry.ContactName.Value, guest.Mobile.Masked(), entry.PartySize.Value, entry.QuotedWaitMinutes,
            entry.JoinedAt.UtcDateTime, 0, entry.Status, null, null, Overdue: false, entry.Version,
            TableReadyStatus: null, TableReadySentAt: null, TableReadyFailed: false);
        await idempotency.CompleteAsync(command.VenueId, Operation, command.IdempotencyKey, item, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogJoined(logger, entry.Id.Value, entry.PartySize.Value);
        return item;
    }

    private static bool IsGuestMobileTaken(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName?.StartsWith("ix_guests_", StringComparison.Ordinal) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "Waitlist entry {WaitlistEntryId} joined (party {PartySize})")]
    private static partial void LogJoined(ILogger logger, Guid waitlistEntryId, int partySize);
}

/// <summary><c>POST /venues/{venueId}/waitlist</c> (Host), optional <c>Idempotency-Key</c>.</summary>
internal sealed class JoinWaitlistEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/venues/{venueId:guid}/waitlist", HandleAsync)
            .WithName("joinWaitlist")
            .WithTags("Waitlist")
            .RequireAuthorization(Policies.Host)
            .WithValidation<JoinWaitlistRequest>()
            .Produces<WaitlistItem>(StatusCodes.Status201Created)
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status422UnprocessableEntity);

    private static async Task<IResult> HandleAsync(
        Guid venueId,
        JoinWaitlistRequest request,
        [FromHeader(Name = IdempotencyKey.Header)] string? idempotencyKey,
        JoinWaitlistHandler handler,
        CancellationToken cancellationToken)
    {
        if (!IdempotencyKey.IsValid(idempotencyKey))
        {
            return ResultToHttp.ToProblem(IdempotencyKey.Invalid);
        }

        var result = await handler.Handle(new JoinWaitlist(new VenueId(venueId), request, idempotencyKey), cancellationToken);
        return result.ToHttp(item => TypedResults.Created((string?)null, item));
    }
}
