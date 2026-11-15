using Microsoft.Extensions.Logging;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// In front of Stripe when the demo is enabled (Design §7.3, agreed 16 Nov): the seed's captured deposits carry
/// <c>pi_seed_…</c> intents that Stripe has never seen, so a refund of one would be refused and retried until the
/// outbox row failed, leaving "Refund pending" on stage. Those refunds go to <see cref="FakePaymentProvider"/>, whose
/// queue the API reports as a signed <c>charge.refunded</c> through the same webhook processing as Stripe's. Everything
/// else, including every booking made during the demo, goes to Stripe unchanged. Registered only when
/// <c>Payments:Provider = Stripe</c> and <c>Demo:Enabled = true</c>; never in the pilot.
/// </summary>
public sealed partial class SeedDepositRouter(IPaymentProvider stripe, FakePaymentProvider seeded, ILogger<SeedDepositRouter> logger)
    : IPaymentProvider
{
    public IPaymentProvider Inner => stripe;

    public Task<PaymentIntentInfo> CreateDepositIntentAsync(
        DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        stripe.CreateDepositIntentAsync(request, idempotencyKey, cancellationToken);

    public Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        if (!SeedPayments.IsSeeded(paymentIntentId))
        {
            return stripe.RefundAsync(paymentIntentId, amount, idempotencyKey, cancellationToken);
        }

        LogSeeded(logger);
        return seeded.RefundAsync(paymentIntentId, amount, idempotencyKey, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refund of a seeded deposit routed to the simulated provider")]
    private static partial void LogSeeded(ILogger logger);
}
