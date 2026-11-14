using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reservation.Tests.Integration.Support;

/// <summary>
/// Stripe webhook bodies and signatures for tests (Design §8.4). Signatures follow Stripe's scheme, written here
/// independently of Stripe.net: <c>t={unix seconds},v1={hex HMAC-SHA256(secret, "{t}.{body}")}</c>.
/// </summary>
public static class StripeEvents
{
    /// <summary>An API version other than the library's, to show the webhook doesn't depend on it.</summary>
    public const string OldApiVersion = "2019-02-19";

    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(body);
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    public static string NewEventId() => $"evt_test_{Guid.NewGuid():N}";

    /// <summary><c>payment_intent.succeeded</c> as Stripe sends it, with the metadata the API set.</summary>
    public static string PaymentSucceeded(
        string paymentIntentId, Guid bookingId, Guid venueId, long amountCents, string? eventId = null, string currency = "nzd",
        string apiVersion = "2026-09-30.clover", string? cardholderName = null) =>
        JsonSerializer.Serialize(new
        {
            id = eventId ?? NewEventId(),
            @object = "event",
            api_version = apiVersion,
            type = "payment_intent.succeeded",
            data = new
            {
                @object = new
                {
                    id = paymentIntentId,
                    @object = "payment_intent",
                    amount = amountCents,
                    amount_received = amountCents,
                    currency,
                    status = "succeeded",
                    metadata = new { bookingId = bookingId.ToString(), venueId = venueId.ToString(), reference = "GDTEST" },
                    shipping = cardholderName is null ? null : new { name = cardholderName },
                },
            },
        });

    /// <summary><c>charge.refunded</c>; newer API versions don't list the refunds, so none by default.</summary>
    public static string ChargeRefunded(
        string paymentIntentId, long amountRefunded, bool refunded = true, string? refundId = null, string? eventId = null) =>
        JsonSerializer.Serialize(new
        {
            id = eventId ?? NewEventId(),
            @object = "event",
            api_version = "2026-09-30.clover",
            type = "charge.refunded",
            data = new
            {
                @object = new
                {
                    id = $"ch_test_{Guid.NewGuid():N}",
                    @object = "charge",
                    payment_intent = paymentIntentId,
                    amount = amountRefunded,
                    amount_refunded = amountRefunded,
                    currency = "nzd",
                    refunded,
                    refunds = refundId is null ? null : new { data = new[] { new { id = refundId } } },
                },
            },
        });

    public static string Other(string type, string? eventId = null) =>
        JsonSerializer.Serialize(new { id = eventId ?? NewEventId(), @object = "event", type, data = new { @object = new { id = "cus_test" } } });
}
