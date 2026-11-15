using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Payments;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Payments;

/// <summary>
/// <c>POST /demo/payments/{bookingId}/succeed</c> (M8b, Design §18.8): the guest page's "Simulate payment (test mode)".
/// Mapped only when <c>Payments:Provider</c> is <c>Fake</c> and <c>Demo:Enabled</c> is true. It builds the
/// <c>payment_intent.succeeded</c> Stripe would send for the booking's intent, signs it with <c>Stripe:WebhookSecret</c>
/// and hands it to <see cref="StripeWebhookProcessor"/>, the same signature check, inbox and <c>CaptureDeposit</c> as a
/// real webhook. It never sets the booking's state itself, so a hold that already ran out takes the late-payment path.
/// The event ID is derived from the intent, so a second call is a duplicate in the inbox. 404 when the booking or its
/// intent doesn't exist.
/// </summary>
internal sealed class SimulatePaymentEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        var services = api.ServiceProvider;
        if (services.GetRequiredService<IOptions<PaymentOptions>>().Value.Provider != PaymentProviderKind.Fake
            || !services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapPost("/demo/payments/{bookingId:guid}/succeed", HandleAsync)
            .WithName("simulatePayment")
            .WithTags("Demo")
            .AllowAnonymous()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesApiProblem(StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> HandleAsync(
        Guid bookingId,
        ReservationsDbContext db,
        StripeWebhookProcessor processor,
        IOptions<StripeOptions> stripe,
        [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var id = new BookingId(bookingId);
        var booking = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (booking?.Deposit is not { PaymentIntentId: { } intentId } deposit)
        {
            return ApiProblems.Result(StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        var body = FakeStripeEvents.PaymentSucceeded(
            intentId, deposit.Amount.Cents, deposit.Amount.Currency, booking.Id.Value, booking.VenueId.Value, booking.Reference.Value);
        var signature = FakeStripeEvents.Sign(body, stripe.Value.WebhookSecret!, clock.GetUtcNow());
        var processed = await processor.ProcessAsync(body, signature, cancellationToken);
        return processed.Rejection is { } rejection
            ? throw new InvalidOperationException($"The simulated payment was rejected: {rejection}.")
            : TypedResults.NoContent();
    }
}

/// <summary>
/// Reports the Fake provider's refunds (M8b, Design §18.8). <see cref="FakePaymentProvider.RefundAsync"/>, called by a
/// refund consumer inside the dispatcher's transaction, only adds to <see cref="FakeRefundQueue"/>; this worker later
/// takes each refund at least <see cref="Delay"/> old (so after that transaction has committed), signs the
/// <c>charge.refunded</c> Stripe would send and hands it to <see cref="StripeWebhookProcessor"/>. The deposit then
/// becomes <c>Refunded</c> exactly as in test mode. A failed report is retried a few times. Runs with
/// <c>Outbox:RunWorkers</c> when the Fake provider is used, or (M10a) in Stripe mode with the demo on, where
/// <c>SeedDepositRouter</c> refunds the seed's deposits through the same queue (Design §7.3); <see cref="RunOnceAsync"/>
/// is the test hook.
/// </summary>
internal sealed partial class FakeRefundReporter(
    IServiceScopeFactory scopeFactory,
    FakeRefundQueue queue,
    IOptions<PaymentOptions> payments,
    IOptions<StripeOptions> stripe,
    IOptions<OutboxOptions> outbox,
    IOptions<DemoOptions> demo,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
    ILogger<FakeRefundReporter> logger,
    WorkerPause? pause = null) : BackgroundService
{
    /// <summary>How old a refund must be before it is reported: after the refund consumer's transaction commits.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    public const int MaxAttempts = 5;

    /// <summary>Reports every due refund; returns how many were processed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var batch = await WorkerPause.EnterAsync(pause, cancellationToken); // the demo reset waits for it (Design §18.10)
        var secret = stripe.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return 0;
        }

        var reported = 0;
        foreach (var refund in queue.TakeDue(clock.GetUtcNow() - Delay))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<StripeWebhookProcessor>();
                var body = FakeStripeEvents.ChargeRefunded(refund.PaymentIntentId, refund.AmountCents, refund.Currency, refund.RefundId);
                var processed = await processor.ProcessAsync(body, FakeStripeEvents.Sign(body, secret, clock.GetUtcNow()), cancellationToken);
                if (processed.Rejection is { } rejection)
                {
                    throw new InvalidOperationException($"The fake refund report was rejected: {rejection}.");
                }

                reported++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (refund.Attempts + 1 < MaxAttempts)
                {
                    queue.Retry(refund with { Attempts = refund.Attempts + 1, RefundedAt = clock.GetUtcNow() });
                }

                LogFailed(logger, refund.RefundId, refund.Attempts + 1, ex);
            }
        }

        return reported;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also in Stripe mode with the demo on: SeedDepositRouter refunds the seed's deposits through the queue (§7.3).
        var queueInUse = payments.Value.Provider == PaymentProviderKind.Fake || demo.Value.Enabled;
        if (!queueInUse || !outbox.Value.RunWorkers)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fake refund {RefundId} not reported (attempt {Attempt})")]
    private static partial void LogFailed(ILogger logger, string refundId, int attempt, Exception error);
}
