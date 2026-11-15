using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.GuestBookings;
using Reservation.Api.Features.Payments;
using Reservation.Api.Features.Reminders;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.GuestLinks;

/// <summary>
/// A Stripe double that, like Stripe, refuses to refund a payment intent it never created (Design §7.3).
/// </summary>
public sealed class StrictStripeDouble : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, bool> _intents = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => [.. _calls];

    public Task<PaymentIntentInfo> CreateDepositIntentAsync(
        DepositIntentRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = $"pi_strict_{request.BookingId.Value:N}";
        _intents[id] = true;
        _calls.Enqueue($"intent:{idempotencyKey}");
        return Task.FromResult(new PaymentIntentInfo(id, $"{id}_secret"));
    }

    public Task<RefundInfo> RefundAsync(string paymentIntentId, Money amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"refund:{paymentIntentId}:{idempotencyKey}");
        return _intents.ContainsKey(paymentIntentId)
            ? Task.FromResult(new RefundInfo($"re_strict_{_calls.Count}"))
            : throw new PaymentProviderException("stripe:invalid_request_error");
    }
}

/// <summary>
/// Stripe mode with the demo on (agreed 16 Nov, Design §7.3): the seed's <c>pi_seed_…</c> deposits are refunded
/// through the simulated path and reach <c>Refunded</c> through the real webhook processing; Stripe sees only the
/// deposits it took itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SeedDepositRefundTests(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    private readonly StrictStripeDouble _stripe = new();

    private TestClock Infra { get; } = new(DateTimeOffset.UtcNow);

    protected override TestClock? InfrastructureClock => Infra;

    protected override IReadOnlyDictionary<string, string?> Settings => StripeSettings(demoEnabled: true);

    internal static Dictionary<string, string?> StripeSettings(bool demoEnabled) => new()
    {
        ["Payments:Provider"] = "Stripe",
        ["Stripe:SecretKey"] = "sk_test_not_a_secret",
        ["Stripe:PublishableKey"] = "pk_test_not_a_secret",
        ["Demo:Enabled"] = demoEnabled ? "true" : "false",
    };

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddKeyedSingleton<IPaymentProvider>(PaymentProviders.StripeKey, _stripe);

    [Fact]
    public void The_router_sits_in_front_of_stripe()
    {
        var provider = Factory.Services.GetRequiredService<IPaymentProvider>();

        Assert.Same(_stripe, Assert.IsType<SeedDepositRouter>(provider).Inner);
    }

    [Fact]
    public async Task Cancelling_seeded_booking_11_from_its_reminder_reaches_Refunded_without_calling_stripe_AC5_5()
    {
        var presenter = await PresenterAsync();
        await ReadAsync<SendRemindersResponse>(await PostAsync(presenter, "/api/v1/demo/reminders/send-now", new { }));
        var booking = await BookingAsync("GDC4L8");
        var reminder = Assert.Single(await MessagesAboutAsync(booking.Id.Value));
        var cancelUrl = Assert.Single(reminder.Actions, a => a.Kind == "cancel").Url;
        var token = cancelUrl[(cancelUrl.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..];

        var cancelled = await ReadAsync<GuestActionResponse>(await PostAsync(NewClient(), "/api/v1/guest/bookings/cancel", new { token }));
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);

        await DispatchAsync();
        Assert.Equal(DepositStatus.Captured, (await BookingAsync("GDC4L8")).Deposit!.Status); // refund requested, not yet reported

        Infra.Advance(FakeRefundReporter.Delay + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Factory.Services.GetRequiredService<FakeRefundReporter>().RunOnceAsync(Ct));
        await DispatchAsync();

        var refunded = await BookingAsync("GDC4L8");
        Assert.Equal(DepositStatus.Refunded, refunded.Deposit!.Status);
        Assert.StartsWith("re_fake_", refunded.Deposit.RefundId, StringComparison.Ordinal);
        Assert.Contains(await MessagesAboutAsync(booking.Id.Value), m => m.Purpose == MessagePurpose.Refund);
        Assert.Empty(_stripe.Calls);
    }

    [Fact]
    public async Task A_deposit_taken_during_the_demo_is_refunded_by_stripe_itself()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 0, 6, mobile: "021 011 0404")), HttpStatusCode.Created);
        var intent = (await BookingAsync(created.Reference)).Deposit!.PaymentIntentId!;
        var paid = StripeEvents.PaymentSucceeded(intent, created.BookingId, GoldenDragon.VenueId.Value, created.Deposit!.AmountCents);
        using (var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/stripe")
               {
                   Content = new StringContent(paid, Encoding.UTF8, "application/json"),
               })
        {
            webhook.Headers.TryAddWithoutValidation("Stripe-Signature", StripeEvents.Sign(paid, ApiFactory.WebhookSecret, Infra.Now));
            Assert.Equal(HttpStatusCode.OK, (await NewClient().SendAsync(webhook, Ct)).StatusCode);
        }

        var host = NewClient();
        await SignInAsync(host);
        var booking = await BookingAsync(created.Reference);
        await ReadAsync<BookingStateResponse>(await PostAsync(host, $"{Venue}/bookings/{created.BookingId}/cancel", new { version = booking.Version }));
        await DispatchAsync();

        var eventId = await EventIdAsync(created.BookingId);
        Assert.Equal([$"intent:deposit:{created.BookingId}", $"refund:{intent}:{eventId}:refund"], _stripe.Calls);
    }

    private async Task<Booking> BookingAsync(string reference)
    {
        await using var db = Database.Context();
        return (await db.Bookings.AsNoTracking().ToListAsync(Ct)).Single(b => b.Reference.Value == reference);
    }

    private async Task<Guid> EventIdAsync(Guid bookingId)
    {
        await using var db = Database.Context();
        return await db.Outbox.Where(o => o.AggregateId == bookingId && o.EventType == "BookingCancelled").Select(o => o.EventId).SingleAsync(Ct);
    }
}

/// <summary>Stripe mode with the demo off (the pilot): no router, the resolved provider is Stripe itself.</summary>
[Collection(PostgresCollection.Name)]
public sealed class StripeWithoutDemoTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => SeedDepositRefundTests.StripeSettings(demoEnabled: false);

    [Fact]
    public void The_seed_deposit_router_is_not_registered()
    {
        var provider = Factory.Services.GetRequiredService<IPaymentProvider>();

        Assert.IsType<StripePaymentProvider>(provider);
        Assert.Same(provider, Factory.Services.GetRequiredKeyedService<IPaymentProvider>(PaymentProviders.StripeKey));
    }
}
