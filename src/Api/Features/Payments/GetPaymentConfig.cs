using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;
using Reservation.Infrastructure.Payments;

namespace Reservation.Api.Features.Payments;

/// <summary>The payment provider as the guest page sees it (<c>stripe</c> or <c>fake</c>).</summary>
internal enum PaymentProviderName
{
    /// <summary>Stripe Elements with <c>publishableKey</c>.</summary>
    Stripe,

    /// <summary>No card form; the demo's "Simulate payment (test mode)" button.</summary>
    Fake,
}

/// <param name="Provider">Which payment step the guest page shows.</param>
/// <param name="PublishableKey">Stripe's publishable key (<c>pk_test_…</c>), only with <c>stripe</c>.</param>
internal sealed record PaymentConfigResponse(PaymentProviderName Provider, string? PublishableKey);

/// <summary>
/// <c>GET /payments/config</c> (M8b, Design §18.8), public: the provider and Stripe's publishable key, so one web build
/// works in every environment. Never a secret key.
/// </summary>
internal sealed class GetPaymentConfigEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/payments/config", Handle)
            .WithName("getPaymentConfig")
            .WithTags("Payments")
            .AllowAnonymous()
            .Produces<PaymentConfigResponse>()
            .ProducesApiProblem(StatusCodes.Status429TooManyRequests);

    private static Ok<PaymentConfigResponse> Handle(IOptions<PaymentOptions> payments, IOptions<StripeOptions> stripe) =>
        TypedResults.Ok(payments.Value.Provider == PaymentProviderKind.Stripe
            ? new PaymentConfigResponse(PaymentProviderName.Stripe, stripe.Value.PublishableKey)
            : new PaymentConfigResponse(PaymentProviderName.Fake, null));
}
