using System.Globalization;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Stripe;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// Stripe (test mode in the demo) behind <see cref="IPaymentProvider"/> (Design §8.1, §8.4). Deposits are card payment
/// intents in the venue currency (NZD) with metadata <c>bookingId</c>, <c>venueId</c> and <c>reference</c>; no names,
/// emails or phone numbers are sent. Every call carries the caller's idempotency key. Stripe errors become a
/// <see cref="PaymentProviderException"/> with Stripe's error type or the HTTP status only.
/// </summary>
public sealed class StripePaymentProvider(IOptions<StripeOptions> options) : IPaymentProvider
{
    private readonly StripeClient _client = new(options.Value.SecretKey
        ?? throw new InvalidOperationException("Stripe:SecretKey is not set."));

    public async Task<PaymentIntentInfo> CreateDepositIntentAsync(
        DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var create = new PaymentIntentCreateOptions
        {
            Amount = request.Amount.Cents,
            Currency = request.Amount.Currency.ToLowerInvariant(),
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true },
            Metadata = new Dictionary<string, string>
            {
                ["bookingId"] = request.BookingId.Value.ToString(),
                ["venueId"] = request.VenueId.Value.ToString(),
                ["reference"] = request.Reference,
            },
        };

        var intent = await CallAsync(() => new PaymentIntentService(_client).CreateAsync(
            create, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken));
        return new PaymentIntentInfo(intent.Id, intent.ClientSecret);
    }

    public async Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var create = new RefundCreateOptions { PaymentIntent = paymentIntentId, Amount = amount.Cents };
        var refund = await CallAsync(() => new RefundService(_client).CreateAsync(
            create, new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken));
        return new RefundInfo(refund.Id);
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (StripeException ex)
        {
            var code = ex.StripeError?.Type ?? string.Create(CultureInfo.InvariantCulture, $"http_{(int)ex.HttpStatusCode}");
            throw new PaymentProviderException($"stripe:{code}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException("stripe:unreachable", ex);
        }
    }
}
