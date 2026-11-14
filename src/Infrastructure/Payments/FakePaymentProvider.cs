using System.Security.Cryptography;
using System.Text;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// The payment provider for Development, integration tests and CI (<c>Payments:Provider = Fake</c>, Design §8.4). It
/// calls nothing and is deterministic: the same idempotency key always gives the same intent, client secret and refund,
/// as Stripe does, so replays behave as in test mode. Nothing ever pays a Fake intent by itself: the demo's "simulate
/// payment" (M8b) or a test posts a signed webhook. A refund is added to <see cref="FakeRefundQueue"/>, which the API
/// later reports as a signed <c>charge.refunded</c> (M8b, Design §18.8); without a queue (some tests) nothing reports it.
/// </summary>
public sealed class FakePaymentProvider(FakeRefundQueue? refunds = null) : IPaymentProvider
{
    public Task<PaymentIntentInfo> CreateDepositIntentAsync(
        DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var id = $"pi_fake_{Digest(idempotencyKey)}";
        return Task.FromResult(new PaymentIntentInfo(id, $"{id}_secret_fake"));
    }

    public Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var refund = new RefundInfo($"re_fake_{Digest(idempotencyKey)}");
        refunds?.Add(paymentIntentId, amount.Cents, amount.Currency, refund.Id);
        return Task.FromResult(refund);
    }

    private static string Digest(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
}
