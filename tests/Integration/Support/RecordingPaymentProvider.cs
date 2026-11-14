using System.Collections.Concurrent;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Payments;

namespace Reservation.Tests.Integration.Support;

/// <summary>One call the API made to the payment provider.</summary>
public sealed record PaymentCall(string Kind, string IdempotencyKey, string? PaymentIntentId, Money Amount, DepositIntentRequest? Request);

/// <summary>
/// The Fake provider with a call log, a failure switch and a hook that runs inside each deposit call (e.g. to check no
/// lock is held). Registered as a singleton in place of <see cref="IPaymentProvider"/>.
/// </summary>
public sealed class RecordingPaymentProvider : IPaymentProvider
{
    private readonly FakePaymentProvider _fake = new();
    private readonly ConcurrentQueue<PaymentCall> _calls = new();

    /// <summary>While true, every call throws <see cref="PaymentProviderException"/> as Stripe being down would.</summary>
    public bool Fail { get; set; }

    /// <summary>Runs at the start of each deposit call, before it answers.</summary>
    public Func<DepositIntentRequest, Task>? OnCreateDeposit { get; set; }

    public IReadOnlyList<PaymentCall> Calls => [.. _calls];

    public IReadOnlyList<PaymentCall> Refunds => [.. _calls.Where(c => c.Kind == "refund")];

    public IReadOnlyList<PaymentCall> Deposits => [.. _calls.Where(c => c.Kind == "deposit")];

    public async Task<PaymentIntentInfo> CreateDepositIntentAsync(
        DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (OnCreateDeposit is not null)
        {
            await OnCreateDeposit(request);
        }

        _calls.Enqueue(new PaymentCall("deposit", idempotencyKey, null, request.Amount, request));
        if (Fail)
        {
            throw new PaymentProviderException("stripe:api_error");
        }

        return await _fake.CreateDepositIntentAsync(request, idempotencyKey, cancellationToken);
    }

    public async Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        _calls.Enqueue(new PaymentCall("refund", idempotencyKey, paymentIntentId, amount, null));
        if (Fail)
        {
            throw new PaymentProviderException("stripe:api_error");
        }

        return await _fake.RefundAsync(paymentIntentId, amount, idempotencyKey, cancellationToken);
    }
}
