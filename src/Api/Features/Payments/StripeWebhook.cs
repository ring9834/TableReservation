using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Payments;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Payments;

/// <summary>What the webhook did with one Stripe event; logged, never returned to Stripe.</summary>
internal enum WebhookOutcome
{
    Applied,
    Duplicate,
    Ignored,
    Refused,
}

/// <summary>
/// Stripe webhooks as commands (Design §5.5, §8.1, §8.4). One transaction: the inbox row <c>('stripe-webhook', event ID)</c>,
/// then the command, then commit. A repeat finds the row and changes nothing. Reads ignore the venue filter (a webhook
/// has no venue) and match the venue from the intent's metadata.
/// <list type="bullet">
/// <item><c>payment_intent.succeeded</c> (CaptureDepositHandler): the booking named by <c>metadata.bookingId</c>, the
/// currency, amount and intent checked. <c>PendingPayment</c> → <c>CaptureDeposit</c> with the table re-checked; once
/// the hold has expired a free table is captured as normal and a taken one cancels with a full refund. A booking the
/// hold expiry already cancelled → <c>RecordLatePayment</c>, refunded through the outbox.</item>
/// <item><c>charge.refunded</c> with the whole charge refunded (RecordRefundHandler): the booking holding the intent →
/// <c>RecordRefund</c>. A partial refund is acknowledged and ignored.</item>
/// <item>Anything else: acknowledged and ignored, with its inbox row.</item>
/// </list>
/// A refusal by a business rule (amounts or intents that don't match, an unknown booking, a transition the booking
/// refuses) keeps the inbox row, writes an <c>audit_log</c> row <c>PaymentWebhookRefused</c> (IDs, code and amounts) and
/// is acknowledged, so Stripe doesn't retry. The money stays with Stripe; see Design §20.
/// </summary>
internal sealed partial class StripeWebhookHandler(
    ReservationsDbContext db,
    RequestContext requestContext,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<StripeWebhookHandler> logger)
{
    public const string Consumer = "stripe-webhook";
    public const string RefusedEventType = "PaymentWebhookRefused";
    public const string UnknownBooking = "unknown_booking";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string AmountMissing = "amount_missing";

    public async Task<WebhookOutcome> HandleAsync(PaymentWebhook webhook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        requestContext.Set(requestContext.CorrelationId, null, Actor.ForSystem());
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var processedAt = infrastructureClock.GetUtcNow().ToUniversalTime();
            var inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO inbox (consumer, message_id, processed_at) VALUES ({Consumer}, {webhook.EventId}, {processedAt}) ON CONFLICT DO NOTHING",
                cancellationToken);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                LogOutcome(logger, webhook.EventId, webhook.Type, WebhookOutcome.Duplicate, null);
                return WebhookOutcome.Duplicate;
            }

            var (outcome, refusal) = webhook.Type switch
            {
                StripeEventTypes.PaymentIntentSucceeded => await CaptureAsync(webhook, cancellationToken),
                StripeEventTypes.ChargeRefunded => await RecordRefundAsync(webhook, cancellationToken),
                _ => (WebhookOutcome.Ignored, null),
            };

            if (refusal is not null)
            {
                db.ChangeTracker.Clear();
                AddRefusal(webhook, refusal);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // The hold expiry changed the booking meanwhile; run the event again on the new state.
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            await transaction.CommitAsync(cancellationToken);
            LogOutcome(logger, webhook.EventId, webhook.Type, outcome, refusal?.Code);
            return outcome;
        }
    }

    private async Task<(WebhookOutcome, Refusal?)> CaptureAsync(PaymentWebhook webhook, CancellationToken cancellationToken)
    {
        if (webhook.BookingId is not { } bookingId || webhook.VenueId is not { } venueId || webhook.PaymentIntentId is not { } intentId)
        {
            return Refuse(UnknownBooking, webhook.VenueId, webhook.BookingId);
        }

        var booking = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .SingleOrDefaultAsync(b => b.Id == bookingId && b.VenueId == venueId, cancellationToken);
        if (booking?.Deposit is not { } deposit)
        {
            return Refuse(UnknownBooking, venueId, bookingId);
        }

        if (!string.Equals(webhook.Currency, deposit.Amount.Currency, StringComparison.Ordinal))
        {
            return Refuse(CurrencyMismatch, venueId, bookingId, webhook.AmountCents, deposit.Amount.Cents);
        }

        if (webhook.AmountCents is not { } cents)
        {
            return Refuse(AmountMissing, venueId, bookingId, null, deposit.Amount.Cents);
        }

        var amount = new Money(cents, deposit.Amount.Currency);
        var now = clock.GetUtcNow();
        var result = booking switch
        {
            { Status: BookingStatus.PendingPayment } => booking.CaptureDeposit(
                intentId, amount, await TableStillFreeAsync(booking, cancellationToken), now, infrastructureClock.GetUtcNow()),
            { Status: BookingStatus.Cancelled, CancellationReason: CancelReason.HoldExpired } => booking.RecordLatePayment(intentId, amount, now),
            _ => booking.CaptureDeposit(intentId, amount, tableStillFree: false, now, infrastructureClock.GetUtcNow()),
        };

        return result.IsSuccess
            ? (WebhookOutcome.Applied, null)
            : Refuse(result.Error!.Code, venueId, bookingId, cents, deposit.Amount.Cents);
    }

    private async Task<(WebhookOutcome, Refusal?)> RecordRefundAsync(PaymentWebhook webhook, CancellationToken cancellationToken)
    {
        if (!webhook.FullyRefunded)
        {
            return (WebhookOutcome.Ignored, null);
        }

        var intentId = webhook.PaymentIntentId;
        var booking = intentId is null
            ? null
            : await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .SingleOrDefaultAsync(b => b.Deposit!.PaymentIntentId == intentId, cancellationToken);
        if (booking?.Deposit is not { } deposit || webhook.RefundId is not { } refundId)
        {
            return Refuse(UnknownBooking, null, null);
        }

        var amount = new Money(webhook.AmountCents ?? deposit.Amount.Cents, deposit.Amount.Currency);
        var result = booking.RecordRefund(refundId, amount, clock.GetUtcNow());
        return result.IsSuccess
            ? (WebhookOutcome.Applied, null)
            : Refuse(result.Error!.Code, booking.VenueId, booking.Id, webhook.AmountCents, deposit.Amount.Cents);
    }

    /// <summary>
    /// §8.1 re-check: no other active booking holds one of the booking's tables over its period. While the booking is
    /// <c>PendingPayment</c> its own assignment is still active, so the exclusion constraint keeps this true in practice.
    /// </summary>
    private async Task<bool> TableStillFreeAsync(Booking booking, CancellationToken cancellationToken)
    {
        var tableIds = booking.Tables.Select(t => t.TableId.Value).ToArray();
        var bookingId = booking.Id.Value;
        var start = booking.Period.Start.ToUniversalTime();
        var end = booking.Period.End.ToUniversalTime();
        var clashes = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM table_assignments
            WHERE active AND booking_id <> {bookingId} AND table_id = ANY({tableIds}) AND period && tstzrange({start}, {end})
            """).SingleAsync(cancellationToken);
        return clashes == 0;
    }

    private void AddRefusal(PaymentWebhook webhook, Refusal refusal)
    {
        if (refusal.VenueId is not { } venueId)
        {
            return;
        }

        var summary = new
        {
            stripeEventId = webhook.EventId,
            stripeEventType = webhook.Type,
            bookingId = refusal.BookingId?.Value,
            paymentIntentId = webhook.PaymentIntentId,
            code = refusal.Code,
            amountCents = refusal.AmountCents,
            expectedCents = refusal.ExpectedCents,
        };
        db.Audit.Add(new AuditEntry(
            venueId,
            Guid.CreateVersion7(infrastructureClock.GetUtcNow()),
            RefusedEventType,
            refusal.BookingId?.Value ?? Guid.Empty,
            JsonSerializer.SerializeToDocument(new { type = "system", id = Consumer }, EventJson.Options),
            clock.GetUtcNow(),
            JsonSerializer.SerializeToDocument(summary, EventJson.Options)));
    }

    private static (WebhookOutcome, Refusal?) Refuse(
        string code, VenueId? venueId, BookingId? bookingId, long? amountCents = null, long? expectedCents = null) =>
        (WebhookOutcome.Refused, new Refusal(code, venueId, bookingId, amountCents, expectedCents));

    private sealed record Refusal(string Code, VenueId? VenueId, BookingId? BookingId, long? AmountCents, long? ExpectedCents);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stripe webhook {StripeEventId} ({StripeEventType}): {Outcome} {Code}")]
    private static partial void LogOutcome(ILogger logger, string stripeEventId, string stripeEventType, WebhookOutcome outcome, string? code);
}

/// <summary>
/// <c>POST /webhooks/stripe</c> (Design §5.5, §14): public, not rate limited (Stripe sends bursts and retries), not in
/// the OpenAPI document (it is for Stripe, not the web client). The body is read up to <see cref="MaxBodyBytes"/>
/// (larger → 413) and checked against the <c>Stripe-Signature</c> header (bad → 401, nothing saved). The body is never
/// logged. With the Stripe CLI: <c>stripe listen --forward-to https://localhost:5001/api/v1/webhooks/stripe</c>.
/// </summary>
internal sealed partial class StripeWebhookEndpoint : IEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;
    public const string SignatureHeader = "Stripe-Signature";

    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/webhooks/stripe", HandleAsync)
            .WithName("stripeWebhook")
            .AllowAnonymous()
            .DisableRateLimiting()
            .ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        StripeWebhookVerifier verifier,
        StripeWebhookHandler handler,
        ILogger<StripeWebhookEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return ApiProblems.Result(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge);
        }

        var parsed = verifier.Parse(body, request.Headers[SignatureHeader].FirstOrDefault());
        if (parsed.IsFailure)
        {
            var code = parsed.Error!.Fields is null ? parsed.Error.Code : StripeWebhookVerifier.InvalidPayload;
            LogRejected(logger, code);
            return code == StripeWebhookVerifier.InvalidPayload
                ? ApiProblems.Result(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed)
                : ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.InvalidSignature);
        }

        await handler.HandleAsync(parsed.Value, cancellationToken);
        return TypedResults.Ok();
    }

    /// <summary>The body as text, or <c>null</c> when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stripe webhook rejected: {Code}")]
    private static partial void LogRejected(ILogger logger, string code);
}
