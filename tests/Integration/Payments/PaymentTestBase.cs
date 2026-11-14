using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Payments;

/// <summary>
/// Payments through the API (M8a, Design §8.4): the recording Fake provider, an infrastructure <see cref="TestClock"/>
/// (the payment hold, webhook signatures and the dispatcher run on it) and the business clock fixed at 18:25.
/// </summary>
public abstract class PaymentTestBase(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    public const string WebhookPath = "/api/v1/webhooks/stripe";

    protected RecordingPaymentProvider Payments { get; } = new();

    /// <summary>The infrastructure clock, started at the real time so sessions and keys behave as usual.</summary>
    protected TestClock Infra { get; } = new(DateTimeOffset.UtcNow);

    protected override TestClock? InfrastructureClock => Infra;

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IPaymentProvider>(Payments);

    protected PaymentHoldExpiryJob HoldExpiry => Factory.Services.GetRequiredService<PaymentHoldExpiryJob>();

    /// <summary>An online booking tomorrow at 19:00; a party of 6+ takes a deposit.</summary>
    private protected async Task<CreateBookingResponse> BookAsync(
        int partySize = 6, string? idempotencyKey = null, string mobile = "021 011 0301", string language = "en", int hour = 19)
    {
        var response = await PostAsync(
            Client, $"{Venue}/bookings", BookingBody(Tomorrow, hour, 0, partySize, mobile: mobile, language: language), idempotencyKey);
        return await ReadAsync<CreateBookingResponse>(response, HttpStatusCode.Created);
    }

    protected async Task<Booking> LoadAsync(Guid bookingId)
    {
        await using var db = Database.Context();
        return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == new BookingId(bookingId), Ct);
    }

    protected async Task<string> IntentOfAsync(Guid bookingId) =>
        (await LoadAsync(bookingId)).Deposit!.PaymentIntentId ?? throw new InvalidOperationException("No payment intent.");

    protected Task<HttpResponseMessage> PostWebhookAsync(string body, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature ?? StripeEvents.Sign(body, ApiFactory.WebhookSecret, Infra.Now));
        return NewClient().SendAsync(request, Ct);
    }

    /// <summary>Stripe reports the deposit paid (amount defaults to the deposit due).</summary>
    private protected async Task<HttpResponseMessage> PaySucceededAsync(CreateBookingResponse booking, long? amountCents = null, string? eventId = null)
    {
        var body = StripeEvents.PaymentSucceeded(
            await IntentOfAsync(booking.BookingId), booking.BookingId, GoldenDragon.VenueId.Value,
            amountCents ?? booking.Deposit!.AmountCents, eventId);
        return await PostWebhookAsync(body);
    }

    protected async Task<int> InboxRowsAsync(string messageId)
    {
        await using var db = Database.Context();
        return await db.Inbox.CountAsync(i => i.Consumer == "stripe-webhook" && i.MessageId == messageId, Ct);
    }

    protected async Task<List<string>> OutboxTypesAsync(Guid bookingId)
    {
        await using var db = Database.Context();
        return await db.Outbox.Where(o => o.AggregateId == bookingId).OrderBy(o => o.Sequence).Select(o => o.EventType).ToListAsync(Ct);
    }

    protected static void AssertOk(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}");
}
