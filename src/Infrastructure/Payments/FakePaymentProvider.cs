using System.Security.Cryptography;
using System.Text;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// The payment provider for Development, integration tests and CI (<c>Payments:Provider = Fake</c>, Design §8.4). It
/// calls nothing and is deterministic: the same idempotency key always gives the same intent and client secret, as
/// Stripe does, so replays behave as in test mode. Nothing ever pays a Fake intent; a test posts a signed webhook.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
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
        return Task.FromResult(new RefundInfo($"re_fake_{Digest(idempotencyKey)}"));
    }

    private static string Digest(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
}
