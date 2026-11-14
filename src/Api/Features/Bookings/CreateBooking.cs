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
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Payments;
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
/// <param name="HoldExpiresAt">When an unpaid hold is released: 10 minutes of real time (M8a, Design §8.4).</param>
/// <param name="ClientSecret">
/// Stripe client secret for the deposit's payment intent (M8a), for Stripe Elements in the guest's browser. Never stored:
/// a replay with the same <c>Idempotency-Key</c> asks Stripe again with the same key and gets the same secret.
/// </param>
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
        // NotEmpty and MaximumLength show the limits in OpenAPI (and so in zod); Must keeps the trimmed check (PersonName).
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("name_length")
            .MaximumLength(PersonName.MaxLength).WithErrorCode("name_length")
            .Must(name => PersonName.Create(name).IsSuccess).WithErrorCode("name_length");
        // NotEmpty puts minLength 1 in OpenAPI (so zod requires it); the NZ mobile check itself stays server-only.
        RuleFor(x => x.Mobile).Cascade(CascadeMode.Stop).NotEmpty().WithErrorCode("mobile_invalid").Custom((mobile, context) =>
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
/// <para>
/// Deposits (M8a, Design §8.4): only after that commit, with no lock held, a <c>PendingPayment</c> booking gets its payment
/// intent (<see cref="IPaymentProvider.CreateDepositIntentAsync"/>, idempotency key <c>deposit:{bookingId}</c>). A second
/// short transaction stores the intent ID on the booking and, with an <c>Idempotency-Key</c>, the response with the new
/// version, still without the client secret. A replay asks the provider again with the same key, so it returns the same
/// intent and secret. A provider failure is 502 <c>payment_provider_error</c>: the booking stays <c>PendingPayment</c>
/// until its hold expires, and the guest retries by sending the same request with the same <c>Idempotency-Key</c>.
/// </para>
/// </summary>
internal sealed partial class BookingCreator(
    ReservationsDbContext db,
    AvailabilityReader reader,
    IdempotencyStore idempotency,
    RequestContext requestContext,
    IOptions<JsonOptions> json,
    IPaymentProvider payments,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<BookingCreator> logger)
{
    public async Task<Result<CreateBookingResponse>> CreateAsync(
        VenueId venueId, BookingChannel channel, CreateBookingRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = await CreateWithRetryAsync(venueId, channel, request, idempotencyKey, cancellationToken);
        return created.IsFailure
            ? created
            : await WithDepositIntentAsync(created.Value, venueId, Operation(channel), idempotencyKey, cancellationToken);
    }

    /// <summary>Stripe idempotency key of a booking's deposit intent (Design §8.4).</summary>
    public static string DepositIdempotencyKey(BookingId bookingId) => $"deposit:{bookingId.Value}";

    private static string Operation(BookingChannel channel) =>
        channel == BookingChannel.Online ? "create-online-booking" : "create-phone-booking";

    private async Task<Result<CreateBookingResponse>> CreateWithRetryAsync(
        VenueId venueId, BookingChannel channel, CreateBookingRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
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

        var operation = Operation(channel);
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
            // Design §20: messages go out in the language active at submit; the stored name is kept, and the name given
            // now is the booking's contact name (M6a).
            guest.UseLanguage(request.Language);
        }

        if (channel == BookingChannel.Online)
        {
            requestContext.Set(requestContext.CorrelationId, requestContext.CausationId, Actor.ForGuest(guest.Id));
        }

        TableId[] tableIds = [slot.Table.Id];
        await TableLocks.LockAsync(db, tableIds, cancellationToken);

        var created = Booking.Create(
            venue, guest.Id, PersonName.Create(request.Name).Value, await BookingReferences.NewAsync(db, cancellationToken), channel, partySize, startsAt, tableIds,
            Blank(request.Notes), Blank(request.Occasion), now, request.Language, holdClockNow: infrastructureClock.GetUtcNow());
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

    /// <summary>
    /// A <c>PendingPayment</c> booking's payment intent, after the create (or a replay) has committed: no lock is held.
    /// Anything else is returned unchanged, as is a replayed booking that is no longer waiting for payment.
    /// </summary>
    private async Task<Result<CreateBookingResponse>> WithDepositIntentAsync(
        CreateBookingResponse response, VenueId venueId, string operation, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (response.Status != BookingStatus.PendingPayment || response.Deposit is null)
        {
            return response;
        }

        db.ChangeTracker.Clear();
        var bookingId = new BookingId(response.BookingId);
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is not { Status: BookingStatus.PendingPayment, Deposit: { } deposit })
        {
            return response;
        }

        PaymentIntentInfo intent;
        try
        {
            intent = await payments.CreateDepositIntentAsync(
                new DepositIntentRequest(booking.Id, venueId, booking.Reference.Value, deposit.Amount),
                DepositIdempotencyKey(booking.Id), cancellationToken);
        }
        catch (Exception ex) when (ex is PaymentProviderException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogProviderFailed(logger, booking.Id.Value, ex is PaymentProviderException ? ex.Message : "timeout");
            return Error.External(ProblemCodes.PaymentProviderError);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tracked = await db.Bookings.SingleAsync(b => b.Id == bookingId, cancellationToken);
            if (tracked.AttachPaymentIntent(intent.Id).IsFailure)
            {
                // A different intent for this booking would mean the provider ignored the idempotency key.
                throw new InvalidOperationException($"Booking {bookingId.Value} already has another payment intent.");
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // The hold expiry or a payment webhook changed the booking meanwhile; read it again.
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var stored = ToResponse(tracked);
            if (idempotencyKey is not null)
            {
                await idempotency.CompleteAsync(
                    venueId, operation, idempotencyKey, StatusCodes.Status201Created,
                    JsonSerializer.Serialize(stored, json.Value.SerializerOptions), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            LogIntentAttached(logger, bookingId.Value);

            // The booking as it is now; only the client secret is added, and it is never stored.
            return tracked.Status == BookingStatus.PendingPayment ? stored with { ClientSecret = intent.ClientSecret } : stored;
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Deposit payment intent attached to booking {BookingId}")]
    private static partial void LogIntentAttached(ILogger logger, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment provider failed creating the deposit for booking {BookingId}: {Code}")]
    private static partial void LogProviderFailed(ILogger logger, Guid bookingId, string code);
}
