using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Payments;

/// <summary>A refund the Fake provider made, waiting to be reported as <c>charge.refunded</c>.</summary>
/// <param name="PaymentIntentId">The refunded intent.</param>
/// <param name="AmountCents">The refunded amount.</param>
/// <param name="Currency">Upper case, e.g. <c>NZD</c>.</param>
/// <param name="RefundId">The Fake's <c>re_fake_…</c> ID.</param>
/// <param name="RefundedAt">When the refund was made, on the infrastructure clock.</param>
/// <param name="Attempts">How many times reporting it has failed.</param>
public sealed record FakeRefund(string PaymentIntentId, long AmountCents, string Currency, string RefundId, DateTimeOffset RefundedAt, int Attempts = 0);

/// <summary>
/// Refunds the Fake provider made (M8b, Design §18.8). <see cref="FakePaymentProvider.RefundAsync"/> only adds to it, as
/// Stripe only records a refund when asked; a worker in the API later reports each one as a signed
/// <c>charge.refunded</c> through the webhook path, outside the dispatcher's transaction that asked for the refund. In
/// memory: a restart loses waiting refunds, which is fine for the Fake.
/// </summary>
public sealed class FakeRefundQueue([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
{
    private readonly ConcurrentQueue<FakeRefund> refunds = new();

    public int Count => refunds.Count;

    public void Add(string paymentIntentId, long amountCents, string currency, string refundId) =>
        refunds.Enqueue(new FakeRefund(paymentIntentId, amountCents, currency, refundId, clock.GetUtcNow()));

    /// <summary>Puts a refund back after a failed report.</summary>
    public void Retry(FakeRefund refund) => refunds.Enqueue(refund);

    /// <summary>
    /// Drops every waiting refund. The demo reset (M10b) calls it: the seeded deposits keep their <c>pi_seed_…</c>
    /// intents across a reset, so a refund made before it must not be reported against the fresh seed.
    /// </summary>
    public void Clear() => refunds.Clear();

    /// <summary>Takes the refunds made at or before <paramref name="madeBy"/>, oldest first; later ones stay.</summary>
    public IReadOnlyList<FakeRefund> TakeDue(DateTimeOffset madeBy)
    {
        var due = new List<FakeRefund>();
        var count = refunds.Count;
        for (var i = 0; i < count && refunds.TryDequeue(out var refund); i++)
        {
            if (refund.RefundedAt <= madeBy)
            {
                due.Add(refund);
            }
            else
            {
                refunds.Enqueue(refund);
            }
        }

        return due;
    }
}
