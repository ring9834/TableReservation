using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Payments;

/// <summary>What a deposit intent is created for. Only IDs, the reference and the amount go to the provider.</summary>
public sealed record DepositIntentRequest(BookingId BookingId, VenueId VenueId, string Reference, Money Amount);

/// <summary>The created intent. <see cref="ClientSecret"/> goes to the guest's browser only, never to logs or storage.</summary>
public sealed record PaymentIntentInfo(string Id, string ClientSecret)
{
    /// <summary>Keeps the client secret out of logs and exception messages.</summary>
    public override string ToString() => $"PaymentIntentInfo {{ Id = {Id} }}";
}

public sealed record RefundInfo(string Id);

/// <summary>
/// The payment provider failed or refused the call. <see cref="Exception.Message"/> carries only a short code (the
/// provider's error type or HTTP status), never the request, response, keys or client secrets.
/// </summary>
public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException()
    {
    }

    public PaymentProviderException(string code)
        : base(code)
    {
    }

    public PaymentProviderException(string code, Exception innerException)
        : base(code, innerException)
    {
    }
}

/// <summary>
/// The single seam to the payment provider for outbound calls (Design §8.1, §8.4). Every call carries an idempotency
/// key, so a retry never takes or refunds twice. Inbound webhooks are checked by <see cref="StripeWebhookVerifier"/>.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Creates (or, with the same key, returns again) the deposit's payment intent in NZD.</summary>
    /// <exception cref="PaymentProviderException">The provider failed.</exception>
    Task<PaymentIntentInfo> CreateDepositIntentAsync(DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Refunds <paramref name="amount"/> of the payment intent; the same key never refunds twice.</summary>
    /// <exception cref="PaymentProviderException">The provider failed.</exception>
    Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken);
}
