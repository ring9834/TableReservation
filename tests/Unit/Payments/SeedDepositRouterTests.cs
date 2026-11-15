using Microsoft.Extensions.Logging.Abstractions;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;

namespace Reservation.Tests.Unit.Payments;

/// <summary><see cref="SeedDepositRouter"/> (Design §7.3): seeded deposits never reach Stripe; everything else does.</summary>
public class SeedDepositRouterTests
{
    private readonly RecordingProvider _stripe = new();

    [Fact]
    public async Task A_seeded_deposit_is_refunded_through_the_simulated_provider()
    {
        var refund = await Router().RefundAsync(SeedPayments.IntentFor("GDC4L8"), Money.FromNzd(12_000), "e1:refund", CancellationToken.None);

        Assert.StartsWith("re_fake_", refund.Id, StringComparison.Ordinal);
        Assert.Empty(_stripe.Calls);
    }

    [Fact]
    public async Task Any_other_refund_and_every_deposit_intent_go_to_stripe()
    {
        var router = Router();

        await router.RefundAsync("pi_3Real", Money.FromNzd(12_000), "e2:refund", CancellationToken.None);
        await router.CreateDepositIntentAsync(
            new DepositIntentRequest(new BookingId(Guid.NewGuid()), new VenueId(Guid.NewGuid()), "GD7K4P", Money.FromNzd(12_000)),
            "deposit:1",
            CancellationToken.None);

        Assert.Equal(["refund:pi_3Real:e2:refund", "intent:deposit:1"], _stripe.Calls);
    }

    [Theory]
    [InlineData("pi_seed_GDC4L8", true)]
    [InlineData("pi_SEED_GDC4L8", false)]
    [InlineData("pi_fake_abc", false)]
    [InlineData("pi_3Real", false)]
    public void Only_the_seed_prefix_counts_as_seeded(string intent, bool seeded) =>
        Assert.Equal(seeded, SeedPayments.IsSeeded(intent));

    private SeedDepositRouter Router() => new(_stripe, new FakePaymentProvider(), NullLogger<SeedDepositRouter>.Instance);

    private sealed class RecordingProvider : IPaymentProvider
    {
        public List<string> Calls { get; } = [];

        public Task<PaymentIntentInfo> CreateDepositIntentAsync(
            DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
        {
            Calls.Add($"intent:{idempotencyKey}");
            return Task.FromResult(new PaymentIntentInfo("pi_3Real", "secret"));
        }

        public Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
        {
            Calls.Add($"refund:{paymentIntentId}:{idempotencyKey}");
            return Task.FromResult(new RefundInfo("re_real"));
        }
    }
}
