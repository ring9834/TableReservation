using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Payments;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Payments;

/// <summary>
/// Requests the refund a cancellation decided (BR-07, Design §8.1, §8.3, §8.4). Consumer <c>refunds</c>, wrapped in the
/// inbox decorator. It never decides: it reads <see cref="BookingCancelled.RefundDecision"/> and calls the provider
/// only when it is <c>full</c>, with idempotency key <c>{eventId}:refund</c>, so a retried handler never refunds twice.
/// A provider failure throws: the savepoint rolls back with the inbox row and the dispatcher retries with its backoff.
/// The deposit becomes <c>Refunded</c> only when Stripe's <c>charge.refunded</c> arrives.
/// </summary>
internal sealed partial class RefundOnCancelHandler(
    ReservationsDbContext db, IPaymentProvider payments, ILogger<RefundOnCancelHandler> logger)
    : IEventHandler<BookingCancelled>
{
    public const string Consumer = "refunds";

    public string ConsumerName => Consumer;

    public static string IdempotencyKey(Guid eventId) => $"{eventId}:refund";

    public async Task HandleAsync(BookingCancelled domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.RefundDecision != RefundDecision.Full)
        {
            return;
        }

        var deposit = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(b => b.Id == domainEvent.BookingId && b.VenueId == domainEvent.VenueId)
            .Select(b => b.Deposit)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Booking {domainEvent.BookingId.Value} has no deposit to refund.");
        if (deposit.PaymentIntentId is not { } intentId)
        {
            throw new InvalidOperationException($"Booking {domainEvent.BookingId.Value} has no payment intent to refund.");
        }

        await payments.RefundAsync(intentId, deposit.Amount, IdempotencyKey(domainEvent.EventId), cancellationToken);
        LogRequested(logger, domainEvent.BookingId.Value, domainEvent.EventId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund requested for booking {BookingId} (event {EventId})")]
    private static partial void LogRequested(ILogger logger, Guid bookingId, Guid eventId);
}

/// <summary>
/// Refunds a payment that arrived after the hold expiry had cancelled the booking (Design §8.1, §12, §8.4). Consumer
/// <c>late-payment-refunds</c>, wrapped in the inbox decorator; idempotency key = the payment intent ID, so the intent
/// is refunded at most once however often this runs.
/// </summary>
internal sealed partial class LatePaymentRefundHandler(IPaymentProvider payments, ILogger<LatePaymentRefundHandler> logger)
    : IEventHandler<LatePaymentReceived>
{
    public const string Consumer = "late-payment-refunds";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(LatePaymentReceived domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await payments.RefundAsync(
            domainEvent.PaymentIntentId, Money.FromNzd(domainEvent.AmountCents), domainEvent.PaymentIntentId, cancellationToken);
        LogRequested(logger, domainEvent.BookingId.Value, domainEvent.EventId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Late payment refund requested for booking {BookingId} (event {EventId})")]
    private static partial void LogRequested(ILogger logger, Guid bookingId, Guid eventId);
}

internal static class PaymentsRegistration
{
    /// <summary>The webhook's command handler and the two refund consumers (Design §8.1, §8.4).</summary>
    public static IServiceCollection AddPaymentHandlers(this IServiceCollection services)
    {
        services.AddScoped<StripeWebhookHandler>();
        services.AddEventHandler<BookingCancelled, RefundOnCancelHandler>();
        services.AddEventHandler<LatePaymentReceived, LatePaymentRefundHandler>();
        return services;
    }
}
