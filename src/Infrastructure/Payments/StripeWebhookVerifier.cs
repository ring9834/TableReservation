using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Stripe;

namespace Reservation.Infrastructure.Payments;

/// <summary>Stripe event types the webhook acts on (Design §5.5, §8.4); every other type is acknowledged and ignored.</summary>
public static class StripeEventTypes
{
    public const string PaymentIntentSucceeded = "payment_intent.succeeded";
    public const string ChargeRefunded = "charge.refunded";
}

/// <summary>
/// The few fields of a Stripe event the webhook needs, read from the raw JSON so the event's API version never matters.
/// Fields that don't apply to the event type are <c>null</c>.
/// </summary>
/// <param name="EventId">Stripe's event ID (<c>evt_…</c>), the inbox message ID.</param>
/// <param name="Type">Stripe's event type.</param>
/// <param name="PaymentIntentId">The intent: <c>data.object.id</c> for intents, <c>data.object.payment_intent</c> for charges.</param>
/// <param name="AmountCents">An intent's <c>amount_received</c> (else <c>amount</c>); a charge's <c>amount_refunded</c>.</param>
/// <param name="Currency">Upper case, e.g. <c>NZD</c>.</param>
/// <param name="BookingId">From the intent's metadata, if it is a UUID.</param>
/// <param name="VenueId">From the intent's metadata, if it is a UUID.</param>
/// <param name="FullyRefunded">A charge's <c>refunded</c> flag (true only when the whole charge is refunded).</param>
/// <param name="RefundId">The latest refund's ID when the payload lists refunds (older API versions); else the charge's ID.</param>
public sealed record PaymentWebhook(
    string EventId,
    string Type,
    string? PaymentIntentId,
    long? AmountCents,
    string? Currency,
    BookingId? BookingId,
    VenueId? VenueId,
    bool FullyRefunded,
    string? RefundId);

/// <summary>
/// Checks a Stripe webhook's signature with <c>Stripe:WebhookSecret</c> (ADR-004 CC2, Design §5.5, §8.4) and reads the
/// fields in <see cref="PaymentWebhook"/>. It uses Stripe.net only for the signature (<c>EventUtility.ValidateSignature</c>,
/// timed on the infrastructure clock) and never deserializes Stripe's typed objects, so an event in another API version
/// than the library's is still accepted. Used with either provider; without a webhook secret every webhook is refused.
/// </summary>
public sealed class StripeWebhookVerifier(
    IOptions<StripeOptions> options,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
{
    public const string InvalidSignature = "invalid_signature";
    public const string NotConfigured = "webhook_secret_missing";
    public const string InvalidPayload = "invalid_payload";

    public Result<PaymentWebhook> Parse(string body, string? signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(body);
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return Error.Forbidden(NotConfigured);
        }

        if (string.IsNullOrEmpty(signatureHeader))
        {
            return Error.Forbidden(InvalidSignature);
        }

        try
        {
            EventUtility.ValidateSignature(
                body, signatureHeader, secret, (long)options.Value.SignatureTolerance.TotalSeconds, clock.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (StripeException)
        {
            return Error.Forbidden(InvalidSignature);
        }

        try
        {
            return Read(body) is { } webhook ? webhook : Error.Validation("body", InvalidPayload);
        }
        catch (JsonException)
        {
            return Error.Validation("body", InvalidPayload);
        }
    }

    private static PaymentWebhook? Read(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "id") is not { } eventId || Text(root, "type") is not { } type)
        {
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return new PaymentWebhook(eventId, type, null, null, null, null, null, false, null);
        }

        var currency = Text(obj, "currency")?.ToUpperInvariant();
        if (type == StripeEventTypes.ChargeRefunded)
        {
            var chargeId = Text(obj, "id");
            var refundId = obj.TryGetProperty("refunds", out var refunds) && refunds.ValueKind == JsonValueKind.Object
                && refunds.TryGetProperty("data", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                    ? Text(list[0], "id")
                    : null;
            var refunded = obj.TryGetProperty("refunded", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new PaymentWebhook(
                eventId, type, Text(obj, "payment_intent"), Number(obj, "amount_refunded"), currency, null, null, refunded, refundId ?? chargeId);
        }

        var metadata = obj.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var bookingId = metadata.ValueKind == JsonValueKind.Object && Guid.TryParse(Text(metadata, "bookingId"), out var b) ? new BookingId(b) : (BookingId?)null;
        var venueId = metadata.ValueKind == JsonValueKind.Object && Guid.TryParse(Text(metadata, "venueId"), out var v) ? new VenueId(v) : (VenueId?)null;
        return new PaymentWebhook(
            eventId, type, Text(obj, "id"), Number(obj, "amount_received") ?? Number(obj, "amount"), currency, bookingId, venueId, false, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
}
