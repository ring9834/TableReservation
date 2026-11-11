using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure.Idempotency;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <summary>An online booking (guest) or a phone booking (host). The table is chosen by BR-05.</summary>
/// <param name="StartsAt">Slot start (UTC or with offset); must be on the venue's slot grid (BR-01).</param>
/// <param name="PartySize">1–20; online 1–8 (BR-04, venue setting).</param>
/// <param name="Name">Guest name, 1–80 characters.</param>
/// <param name="Mobile">NZ mobile in any common format; stored as E.164 (BR-12).</param>
/// <param name="Language">The language active when the guest submitted; messages go out in it.</param>
/// <param name="Email">Optional.</param>
/// <param name="AllergyNotes">Optional, up to 500 characters; kept on the guest.</param>
/// <param name="Occasion">Optional, up to 50 characters.</param>
/// <param name="Notes">Optional booking notes, up to 500 characters.</param>
internal sealed record CreateBookingRequest(
    DateTimeOffset StartsAt,
    int PartySize,
    string Name,
    string Mobile,
    Language Language,
    string? Email = null,
    string? AllergyNotes = null,
    string? Occasion = null,
    string? Notes = null);

/// <param name="Version">Row version; send it back with later changes.</param>
/// <param name="Deposit">Due for an online party of 6 or more (BR-06); the booking is then <c>pendingPayment</c>.</param>
/// <param name="HoldExpiresAt">When an unpaid hold is released (10 minutes).</param>
/// <param name="ClientSecret">Stripe client secret for the deposit; arrives with payments (M8).</param>
internal sealed record CreateBookingResponse(
    Guid BookingId,
    string Reference,
    BookingStatus Status,
    uint Version,
    DateTime StartsAt,
    DateTime EndsAt,
    int PartySize,
    IReadOnlyList<Guid> TableIds,
    MoneyDto? Deposit,
    DateTime? HoldExpiresAt,
    string? ClientSecret);

internal sealed class CreateBookingRequestValidator : AbstractValidator<CreateBookingRequest>
{
    public const int MaxEmailLength = 254;
    public const int MaxNotesLength = 500;
    public const int MaxOccasionLength = 50;

    public CreateBookingRequestValidator()
    {
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithErrorCode("starts_at_required");
        RuleFor(x => x.PartySize).InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
        RuleFor(x => x.Name).Must(name => PersonName.Create(name).IsSuccess).WithErrorCode("name_length");
        RuleFor(x => x.Mobile).Custom((mobile, context) =>
        {
            var parsed = PhoneNumber.Create(mobile);
            if (parsed.IsFailure)
            {
                context.AddFailure(new ValidationFailure(nameof(CreateBookingRequest.Mobile), "Invalid mobile number.")
                {
                    ErrorCode = parsed.Error!.Fields?.Values.SelectMany(v => v).FirstOrDefault() ?? "mobile_invalid",
                });
            }
        });
        RuleFor(x => x.Email!).MaximumLength(MaxEmailLength).WithErrorCode("email_too_long")
            .EmailAddress().WithErrorCode("email_invalid")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.AllergyNotes).MaximumLength(MaxNotesLength).WithErrorCode("allergy_notes_too_long");
        RuleFor(x => x.Notes).MaximumLength(MaxNotesLength).WithErrorCode("notes_too_long");
        RuleFor(x => x.Occasion).MaximumLength(MaxOccasionLength).WithErrorCode("occasion_too_long");
        RuleFor(x => x.Language).IsInEnum().WithErrorCode("language_invalid");
    }
}

/// <summary>The optional <c>Idempotency-Key</c> header (ADR-004).</summary>
internal static class IdempotencyKey
{
    public const string Header = "Idempotency-Key";
    public const int MaxLength = 100;

    public static bool IsValid(string? key) =>
        key is null || (key.Length is > 0 and <= MaxLength && key.All(c => c is >= '!' and <= '~'));

    public static Error Invalid { get; } = Error.Validation("idempotencyKey", "idempotency_key_invalid");
}

/// <summary>
/// <c>CreateOnlineBooking</c> and <c>CreatePhoneBooking</c> (Design §9.2, §11, §12), one transaction:
/// claim the idempotency key → take <c>pg_advisory_xact_lock</c> for the venue and slot → count covers (BR-03) and
/// pick the smallest free table (BR-05) → find or create the guest by mobile → lock each chosen table (ascending ID)
/// → <c>Booking.Create</c> → save (the
/// outbox rows go in the same save) → store the response under the key → commit. Pacing over the limit or no free
/// table is 409 <c>slot_taken</c>; so is the exclusion constraint (<c>23P01</c>) at save time, mapped centrally.
/// </summary>
internal sealed partial class BookingCreator(
    ReservationsDbContext db,
    AvailabilityReader reader,
    IdempotencyStore idempotency,
    RequestContext requestContext,
    IOptions<JsonOptions> json,
    TimeProvider clock,
    ILogger<BookingCreator> logger)
{
    private const int MaxReferenceAttempts = 5;

    public async Task<Result<CreateBookingResponse>> CreateAsync(
        VenueId venueId, BookingChannel channel, CreateBookingRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateOnceAsync(venueId, channel, request, idempotencyKey, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt == 1 && IsGuestMobileTaken(ex))
            {
                // Two first bookings for one new mobile at once: the guest now exists, so the retry reuses it.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<Result<CreateBookingResponse>> CreateOnceAsync(
        VenueId venueId, BookingChannel channel, CreateBookingRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var settings = venue.Settings;
        var timeZone = venue.FindTimeZone();
        var startsAt = request.StartsAt.ToUniversalTime();
        var partySize = PartySize.Of(request.PartySize);
        if (!settings.IsWithinBookingWindow(AvailabilityCalculator.LocalDate(startsAt, timeZone), AvailabilityCalculator.LocalDate(now, timeZone)))
        {
            return Error.Rule(ErrorCodes.OutsideBookingWindow);
        }

        if (channel == BookingChannel.Online && partySize.Value > settings.MaxOnlinePartySize)
        {
            return Error.Rule(ErrorCodes.PartyTooLarge);
        }

        if (startsAt <= now || !settings.IsBookableSlot(startsAt, timeZone))
        {
            return Error.Rule(ErrorCodes.OutsideServiceHours);
        }

        var operation = channel == BookingChannel.Online ? "create-online-booking" : "create-phone-booking";
        var requestHash = idempotencyKey is null ? null : Hash(request);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (idempotencyKey is not null)
        {
            var earlier = await idempotency.TryClaimAsync(venueId, operation, idempotencyKey, requestHash!, cancellationToken);
            if (earlier is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (!string.Equals(earlier.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return Error.Rule(ProblemCodes.IdempotencyKeyReused);
                }

                LogReplayed(logger, operation);
                return JsonSerializer.Deserialize<CreateBookingResponse>(earlier.Response, json.Value.SerializerOptions)!;
            }
        }

        // §12: one lock per venue and slot, held until commit; a second request for the slot waits, then counts again.
        var lockKey = $"{venueId.Value}:{startsAt.UtcDateTime:O}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);

        var day = await reader.LoadAsync(venue, AvailabilityCalculator.LocalDate(startsAt, timeZone), now, cancellationToken);
        var slot = AvailabilityCalculator.Evaluate(day, startsAt, partySize);
        if (slot is null)
        {
            return Error.Conflict(ErrorCodes.SlotTaken);
        }

        var mobile = PhoneNumber.Create(request.Mobile).Value;
        var guest = await db.Guests.SingleOrDefaultAsync(g => g.Mobile == mobile, cancellationToken);
        if (guest is null)
        {
            guest = Guest.Create(
                venueId, PersonName.Create(request.Name).Value, mobile, request.Email, request.Language, request.AllergyNotes, now);
            db.Guests.Add(guest);
        }
        else
        {
            // Design §20: messages go out in the language active at submit; the stored name is kept.
            guest.UseLanguage(request.Language);
        }

        if (channel == BookingChannel.Online)
        {
            requestContext.Set(requestContext.CorrelationId, requestContext.CausationId, Actor.ForGuest(guest.Id));
        }

        TableId[] tableIds = [slot.Table.Id];
        await LockTablesAsync(tableIds, cancellationToken);

        var created = Booking.Create(
            venue, guest.Id, await NewReferenceAsync(cancellationToken), channel, partySize, startsAt, tableIds,
            Blank(request.Notes), Blank(request.Occasion), now);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var booking = created.Value;
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(cancellationToken);

        var response = ToResponse(booking);
        if (idempotencyKey is not null)
        {
            await idempotency.CompleteAsync(
                venueId, operation, idempotencyKey, StatusCodes.Status201Created,
                JsonSerializer.Serialize(response, json.Value.SerializerOptions), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        LogCreated(logger, booking.Id.Value, channel, booking.Status);
        return response;
    }

    private static CreateBookingResponse ToResponse(Booking booking) => new(
        booking.Id.Value,
        booking.Reference.Value,
        booking.Status,
        booking.Version,
        booking.Period.Start.UtcDateTime,
        booking.Period.End.UtcDateTime,
        booking.PartySize.Value,
        booking.Tables.Select(t => t.TableId.Value).ToList(),
        booking.Deposit is { } deposit ? MoneyDto.From(deposit.Amount) : null,
        booking.HoldExpiresAt?.UtcDateTime,
        ClientSecret: null);

    /// <summary>
    /// §12: overlapping slots take different slot locks, so two requests can pick the same table at once. Inserting
    /// both assignments concurrently can deadlock inside the exclusion constraint (<c>40P01</c>); a lock per table,
    /// taken in ascending ID order and held until commit, makes the second wait and then fail cleanly with
    /// <c>23P01</c> → <c>slot_taken</c>.
    /// </summary>
    private async Task LockTablesAsync(IEnumerable<TableId> tableIds, CancellationToken cancellationToken)
    {
        foreach (var tableId in tableIds.Select(t => t.Value).Distinct().Order())
        {
            var lockKey = $"table:{tableId}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        }
    }

    private async Task<BookingReference> NewReferenceAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxReferenceAttempts; i++)
        {
            var reference = BookingReference.Generate(Random.Shared);
            if (!await db.Bookings.AnyAsync(b => b.Reference == reference, cancellationToken))
            {
                return reference;
            }
        }

        throw new InvalidOperationException("Could not generate a unique booking reference.");
    }

    private string Hash(CreateBookingRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, json.Value.SerializerOptions))));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsGuestMobileTaken(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName?.StartsWith("ix_guests_", StringComparison.Ordinal) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "Booking {BookingId} created ({Channel}, {Status})")]
    private static partial void LogCreated(ILogger logger, Guid bookingId, BookingChannel channel, BookingStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotent replay of {Operation}")]
    private static partial void LogReplayed(ILogger logger, string operation);
}
