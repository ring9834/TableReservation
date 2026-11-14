using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// Stripe events made in-process for the Fake provider (M8b, Design §18.8): the bodies <see cref="StripeWebhookVerifier"/>
/// reads and a <c>Stripe-Signature</c> header made with <c>Stripe:WebhookSecret</c>, so a simulated payment or refund goes
/// through the same signature check, inbox and handler as a real webhook. Event IDs are derived from the intent or the
/// refund, so the same simulation sent twice is a duplicate in the inbox.
/// </summary>
public static class FakeStripeEvents
{
    /// <summary>A <c>payment_intent.succeeded</c> for the whole amount, with the metadata the deposit intent carries.</summary>
    public static string PaymentSucceeded(string paymentIntentId, long amountCents, string currency, Guid bookingId, Guid venueId, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        return JsonSerializer.Serialize(new
        {
            id = EventId("succeeded", paymentIntentId),
            @object = "event",
            type = StripeEventTypes.PaymentIntentSucceeded,
            livemode = false,
            data = new
            {
                @object = new
                {
                    id = paymentIntentId,
                    @object = "payment_intent",
                    amount = amountCents,
                    amount_received = amountCents,
                    currency = currency.ToLowerInvariant(),
                    status = "succeeded",
                    metadata = new
                    {
                        bookingId = bookingId.ToString(),
                        venueId = venueId.ToString(),
                        reference,
                    },
                },
            },
        });
    }

    /// <summary>A <c>charge.refunded</c> with the whole charge refunded by <paramref name="refundId"/>.</summary>
    public static string ChargeRefunded(string paymentIntentId, long amountCents, string currency, string refundId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refundId);
        return JsonSerializer.Serialize(new
        {
            id = EventId("refunded", refundId),
            @object = "event",
            type = StripeEventTypes.ChargeRefunded,
            livemode = false,
            data = new
            {
                @object = new
                {
                    id = $"ch_fake_{Digest(paymentIntentId)}",
                    @object = "charge",
                    payment_intent = paymentIntentId,
                    amount = amountCents,
                    amount_refunded = amountCents,
                    currency = currency.ToLowerInvariant(),
                    refunded = true,
                    refunds = new { data = new[] { new { id = refundId, amount = amountCents } } },
                },
            },
        });
    }

    /// <summary>A <c>Stripe-Signature</c> header value as Stripe makes it: <c>t={unix},v1={HMAC-SHA256(secret, "{t}.{body}")}</c>.</summary>
    public static string Sign(string body, string secret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    private static string EventId(string kind, string source) => $"evt_fake_{Digest($"{kind}:{source}")}";

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}
