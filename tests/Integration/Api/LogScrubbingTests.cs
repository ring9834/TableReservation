using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Messages;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// ADR-004 "What is never logged": a sample booking goes through the API and the captured log output (messages,
/// properties, scopes, exceptions) must not contain any of its personal data or the request body.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogScrubbingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Name = "Zyxa Quillfeather";
    private const string Mobile = "021 011 0166";
    private const string Email = "zyxa.quillfeather@example.org";
    private const string Notes = "Scrub-note-7f3a";
    private const string Allergy = "Scrub-allergy-9c1e";
    private const string Occasion = "Scrub-occasion-2b8d";

    [Fact]
    public async Task Booking_requests_leave_no_personal_data_in_the_logs()
    {
        var body = BookingBody(DemoTime.Tonight, 19, 30, 2, Name, Mobile, Email, Notes, Allergy, Occasion);
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Venue + "/bookings", body, "scrub-key"), HttpStatusCode.Created);
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Venue + "/bookings", body, "scrub-key"), HttpStatusCode.Created);
        await PostAsync(Client, Venue + "/bookings", BookingBody(DemoTime.Tonight, 19, 30, 2, Name, Mobile, "not-an-email " + Email, Notes));
        await SignInAsync(Client);
        await ReadAsync<BookingListResponse>(await Client.GetAsync(Venue + "/bookings", Ct)); // names in the response only

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("HTTP POST /api/v1/venues/{venueId:guid}/bookings responded 201", log, StringComparison.Ordinal);
        string[] secrets =
        [
            Name, "Quillfeather", Email, Notes, Allergy, Occasion, ApiFactory.HostPasscode,
            "021 011 0166", "0210110166", "+64210110166", "210110166", "__Host-demo-session",
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Walk_in_and_waitlist_requests_leave_no_names_or_mobiles_in_the_logs()
    {
        const string walkInName = "Vexmor Plinthwick";
        const string waitingName = "Quorra Ashendell";
        const string waitingMobile = "021 011 0167";
        await SignInAsync(Client);
        var t1 = Reservation.Infrastructure.Demo.GoldenDragon.TableId("T1").Value;
        var t4 = Reservation.Infrastructure.Demo.GoldenDragon.TableId("T4").Value;

        await ReadAsync<Reservation.Api.Features.Seating.SeatWalkInResponse>(
            await PostAsync(Client, Venue + "/walk-ins", new { partySize = 2, tableId = t1, name = walkInName }, "scrub-walk-in"),
            HttpStatusCode.Created);
        var entry = await ReadAsync<Reservation.Api.Features.Waitlist.WaitlistItem>(
            await PostAsync(Client, Venue + "/waitlist", new { name = waitingName, mobile = waitingMobile, partySize = 4, quotedWaitMinutes = 35 }),
            HttpStatusCode.Created);
        await PostAsync(Client, Venue + "/waitlist", new { name = waitingName, mobile = "not a mobile", partySize = 4, quotedWaitMinutes = 35 });
        var notified = await ReadAsync<Reservation.Api.Features.Waitlist.WaitlistEntryStateResponse>(
            await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/notify", new { tableId = t4, entry.Version }));
        await ReadAsync<Reservation.Api.Features.Waitlist.WaitlistResponse>(await Client.GetAsync(Venue + "/waitlist", Ct));
        await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/end", new { outcome = "walkedAway", notified.Version });

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("HTTP POST /api/v1/venues/{venueId:guid}/walk-ins responded 201", log, StringComparison.Ordinal);
        Assert.Contains("HTTP POST /api/v1/venues/{venueId:guid}/waitlist responded 201", log, StringComparison.Ordinal);
        string[] secrets =
        [
            walkInName, "Plinthwick", waitingName, "Ashendell", "not a mobile",
            "021 011 0167", "0210110167", "+64210110167", "210110167", "__Host-demo-session",
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }
    [Fact]
    public async Task Messages_queued_sent_and_logged_leave_no_bodies_names_or_mobiles_in_the_logs()
    {
        const string bookingName = "Oriel Thistlewane";
        const string bookingMobile = "021 011 0168";
        const string waitingName = "Brannoch Velloway";
        const string waitingMobile = "021 011 0169";
        var t4 = Reservation.Infrastructure.Demo.GoldenDragon.TableId("T4").Value;
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + "/bookings", BookingBody(DemoTime.Tonight.AddDays(1), 19, 30, 2, bookingName, bookingMobile)),
            HttpStatusCode.Created);
        await SignInAsync(Client);
        var entry = await ReadAsync<Reservation.Api.Features.Waitlist.WaitlistItem>(
            await PostAsync(Client, Venue + "/waitlist", new { name = waitingName, mobile = waitingMobile, partySize = 4, quotedWaitMinutes = 35, language = "zh" }),
            HttpStatusCode.Created);
        await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/notify", new { tableId = t4, entry.Version });

        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher>();
        var worker = Factory.Services.GetRequiredService<MessageDeliveryWorker>();
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
        }

        while (await worker.DeliverBatchAsync(Ct) > 0)
        {
        }

        using var presenter = NewClient();
        await SignInAsync(presenter, ApiFactory.PresenterPasscode);
        var messages = await ReadAsync<MessageLogResponse>(await presenter.GetAsync(Venue + "/messages", Ct));
        Assert.Equal(2, messages.Items.Count);
        Assert.All(messages.Items, m => Assert.Equal(MessageStatus.Sent, m.Status));

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("Messages: ", log, StringComparison.Ordinal); // the worker did log, by ID
        string[] secrets =
        [
            bookingName, "Thistlewane", waitingName, "Velloway", booking.Reference,
            "you're booked", "table is ready", "餐桌", "Golden Dragon:", "Golden Dragon：",
            "021 011 0168", "0210110168", "+64210110168", "210110168",
            "021 011 0169", "0210110169", "+64210110169", "210110169", "__Host-demo-session",
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Deposits_webhooks_and_refunds_leave_no_secrets_bodies_names_or_mobiles_in_the_logs()
    {
        const string name = "Ysolde Marrowgate";
        const string mobile = "021 011 0170";
        const string cardholder = "Scrub-cardholder-5e2c";
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + "/bookings", BookingBody(DemoTime.Tonight.AddDays(1), 19, 0, 6, name, mobile), "scrub-deposit"),
            HttpStatusCode.Created);
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, Venue + "/bookings", BookingBody(DemoTime.Tonight.AddDays(1), 19, 0, 6, name, mobile), "scrub-deposit"),
            HttpStatusCode.Created);
        var intent = booking.ClientSecret!.Replace("_secret_fake", string.Empty, StringComparison.Ordinal);

        var paid = StripeEvents.PaymentSucceeded(
            intent, booking.BookingId, Reservation.Infrastructure.Demo.GoldenDragon.VenueId.Value, 12_000, cardholderName: cardholder);
        var forgedSignature = StripeEvents.Sign(paid, "whsec_forged_scrub", DateTimeOffset.UtcNow);
        await SendWebhookAsync(paid, forgedSignature);
        var goodSignature = StripeEvents.Sign(paid, ApiFactory.WebhookSecret, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(paid, goodSignature)).StatusCode);

        await SignInAsync(Client);
        var version = (await ReadAsync<BookingListResponse>(await Client.GetAsync($"{Venue}/bookings?day={DemoTime.Tonight.AddDays(1):yyyy-MM-dd}", Ct)))
            .Items.Single(i => i.Id == booking.BookingId).Version;
        await ReadAsync<BookingStateResponse>(await PostAsync(Client, $"{Venue}/bookings/{booking.BookingId}/cancel", new { version }));
        await DispatchAndDeliverAsync();
        var refunded = StripeEvents.ChargeRefunded(intent, 12_000);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(refunded, StripeEvents.Sign(refunded, ApiFactory.WebhookSecret, DateTimeOffset.UtcNow))).StatusCode);
        await DispatchAndDeliverAsync();

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("Stripe webhook rejected", log, StringComparison.Ordinal); // the forged one, by code only
        Assert.Contains("Refund requested for booking", log, StringComparison.Ordinal);
        string[] secrets =
        [
            booking.ClientSecret, "_secret_", ApiFactory.WebhookSecret, "whsec_", forgedSignature, goodSignature, "v1=",
            cardholder, "payment_intent.succeeded\"", "\"amount_received\"", "\"metadata\"",
            name, "Marrowgate", booking.Reference, "is cancelled", "refunded your deposit", "NZ$",
            "021 011 0170", "0210110170", "+64210110170", "210110170", "__Host-demo-session",
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }

    private Task<HttpResponseMessage> SendWebhookAsync(string body, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/stripe")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return NewClient().SendAsync(request, Ct);
    }

    private async Task DispatchAndDeliverAsync()
    {
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher>();
        var worker = Factory.Services.GetRequiredService<MessageDeliveryWorker>();
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
        }

        while (await worker.DeliverBatchAsync(Ct) > 0)
        {
        }
    }
}
