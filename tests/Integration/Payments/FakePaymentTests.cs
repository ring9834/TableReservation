using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Payments;
using Reservation.Api.Features.Venues;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Payments;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Payments;

/// <summary>
/// The Fake provider end to end (M8b, Design §18.8): the demo's simulated payment and the Fake's refunds both become
/// signed Stripe events that take the real webhook path (signature check, inbox, command). Uses the real
/// <see cref="FakePaymentProvider"/>, not the recording one, so refunds reach <see cref="FakeRefundQueue"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FakePaymentTests(PostgresFixture postgres) : PaymentTestBase(postgres)
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        // The registered Fake provider, with its refund queue.
    }

    private FakeRefundReporter Reporter => Factory.Services.GetRequiredService<FakeRefundReporter>();

    private FakeRefundQueue RefundQueue => Factory.Services.GetRequiredService<FakeRefundQueue>();

    private Task<HttpResponseMessage> SimulateAsync(Guid bookingId) =>
        NewClient().PostAsync($"/api/v1/demo/payments/{bookingId}/succeed", null, Ct);

    [Fact]
    public async Task Simulate_payment_captures_the_deposit_through_the_signed_webhook_path_once()
    {
        var booking = await BookAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await SimulateAsync(booking.BookingId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SimulateAsync(booking.BookingId)).StatusCode); // a double tap

        var paid = await LoadAsync(booking.BookingId);
        Assert.Equal(BookingStatus.Booked, paid.Status);
        Assert.Equal(DepositStatus.Captured, paid.Deposit!.Status);
        Assert.Equal(["BookingCreated", "DepositCaptured"], await OutboxTypesAsync(booking.BookingId));

        await using var db = Database.Context();
        var inbox = await db.Inbox.Where(i => i.Consumer == StripeWebhookHandler.Consumer).Select(i => i.MessageId).ToListAsync(Ct);
        Assert.StartsWith("evt_fake_", Assert.Single(inbox), StringComparison.Ordinal); // one event, the repeat a duplicate
    }

    [Fact]
    public void The_simulate_route_is_mapped_with_the_fake_provider_and_the_demo_on() =>
        Assert.True(StripeProviderRouteTests.SimulateRouteMapped(Factory));

    [Fact]
    public async Task Simulate_payment_for_an_unknown_booking_or_one_without_a_deposit_is_404()
    {
        await ProblemAssert.IsAsync(await SimulateAsync(Guid.NewGuid()), HttpStatusCode.NotFound, "not_found");

        var small = await BookAsync(partySize: 2);
        await ProblemAssert.IsAsync(await SimulateAsync(small.BookingId), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_fake_refund_is_reported_after_the_refund_commits_and_reaches_refunded_once_AC5_5()
    {
        var booking = await BookAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SimulateAsync(booking.BookingId)).StatusCode);
        await DispatchAsync();

        var host = NewClient();
        await SignInAsync(host);
        var version = (await LoadAsync(booking.BookingId)).Version;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host, $"{Venue}/bookings/{booking.BookingId}/cancel", new { version })).StatusCode);
        Assert.Equal(0, RefundQueue.Count); // nothing is refunded inside the cancel request

        await DispatchAsync();
        Assert.Equal(1, RefundQueue.Count);
        Assert.Equal(0, await Reporter.RunOnceAsync(Ct)); // not before the refund's transaction has had time to commit
        Assert.Equal(DepositStatus.Captured, (await LoadAsync(booking.BookingId)).Deposit!.Status);

        Infra.Advance(FakeRefundReporter.Delay);
        Assert.Equal(1, await Reporter.RunOnceAsync(Ct));
        var refunded = await LoadAsync(booking.BookingId);
        Assert.Equal(DepositStatus.Refunded, refunded.Deposit!.Status);
        Assert.StartsWith("re_fake_", refunded.Deposit.RefundId, StringComparison.Ordinal);

        // The same refund reported again (Stripe resends, or a retried refund with the same key): a duplicate in the inbox.
        RefundQueue.Add(refunded.Deposit.PaymentIntentId!, 12_000, "NZD", refunded.Deposit.RefundId!);
        Infra.Advance(FakeRefundReporter.Delay);
        Assert.Equal(1, await Reporter.RunOnceAsync(Ct));
        await DispatchAsync();

        await using var db = Database.Context();
        Assert.Equal(2, await db.Inbox.CountAsync(i => i.Consumer == StripeWebhookHandler.Consumer, Ct)); // payment + one refund
        Assert.Equal(["BookingCreated", "DepositCaptured", "BookingCancelled", "DepositRefunded"], await OutboxTypesAsync(booking.BookingId));
        Assert.Single(await MessagesAboutAsync(booking.BookingId), m => m.Purpose == MessagePurpose.Refund);
    }

    [Fact]
    public async Task The_status_poll_counts_the_hold_down_on_the_infrastructure_clock()
    {
        var booking = await BookAsync();

        Assert.Equal(600, (await StatusAsync(booking.BookingId)).HoldSecondsRemaining);
        Infra.Advance(TimeSpan.FromSeconds(19.5));
        Assert.Equal(581, (await StatusAsync(booking.BookingId)).HoldSecondsRemaining); // rounded up
        Infra.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, (await StatusAsync(booking.BookingId)).HoldSecondsRemaining); // never negative

        var paid = await BookAsync(hour: 20, mobile: "021 011 0302");
        Assert.Equal(HttpStatusCode.NoContent, (await SimulateAsync(paid.BookingId)).StatusCode);
        var status = await StatusAsync(paid.BookingId);
        Assert.Equal(BookingStatus.Booked, status.Status);
        Assert.Null(status.HoldSecondsRemaining);
    }

    [Fact]
    public async Task The_host_sees_the_deposit_on_the_floor_plan_and_the_refund_decision_in_tonights_list()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(DemoTime.Tonight, 20, 0, 6)), HttpStatusCode.Created);
        var host = NewClient();
        await SignInAsync(host);

        var held = await NextOnAsync(host, created.BookingId);
        Assert.Equal(BookingStatus.PendingPayment, held.Status);
        Assert.Equal(12_000, held.Deposit!.AmountCents);
        Assert.Equal(DepositStatus.Pending, held.DepositStatus);

        Assert.Equal(HttpStatusCode.NoContent, (await SimulateAsync(created.BookingId)).StatusCode);
        Assert.Equal(DepositStatus.Captured, (await NextOnAsync(host, created.BookingId)).DepositStatus);
        Assert.Null((await ListItemAsync(host, created.BookingId)).RefundDecision);

        var version = (await LoadAsync(created.BookingId)).Version;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(host, $"{Venue}/bookings/{created.BookingId}/cancel", new { version })).StatusCode);
        var cancelled = await ListItemAsync(host, created.BookingId);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);
        Assert.Equal(DepositStatus.Captured, cancelled.DepositStatus); // "Refund pending" until charge.refunded
    }

    private async Task<BookingStatusResponse> StatusAsync(Guid bookingId) =>
        await ReadAsync<BookingStatusResponse>(await NewClient().GetAsync($"{Venue}/bookings/{bookingId}/status", Ct));

    private static async Task<TableBookingSummary> NextOnAsync(HttpClient host, Guid bookingId)
    {
        var plan = await ReadAsync<FloorPlanResponse>(await host.GetAsync($"{Venue}/floor-plan", Ct));
        return plan.Tables.Select(t => t.Next).Single(b => b?.BookingId == bookingId)!;
    }

    private static async Task<BookingListItem> ListItemAsync(HttpClient host, Guid bookingId)
    {
        var list = await ReadAsync<BookingListResponse>(await host.GetAsync($"{Venue}/bookings", Ct));
        return list.Items.Single(i => i.Id == bookingId);
    }
}

/// <summary>
/// <c>GET /payments/config</c> with the Fake provider, and the start-up checks M8b added (Design §18.8).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PaymentConfigTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task The_fake_provider_has_no_publishable_key_and_needs_no_sign_in()
    {
        var response = await NewClient().GetAsync("/api/v1/payments/config", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("fake", json.RootElement.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("publishableKey").ValueKind);
    }

    [Theory]
    [InlineData("Stripe", null, "Stripe:PublishableKey")]
    [InlineData("Fake", "true", "Stripe:WebhookSecret")]
    public async Task Start_up_names_the_missing_setting(string provider, string? demoEnabled, string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Payments:Provider"] = provider,
            ["Stripe:SecretKey"] = "sk_test_not_a_secret",
            ["Stripe:WebhookSecret"] = provider == "Fake" ? string.Empty : ApiFactory.WebhookSecret,
            ["Stripe:PublishableKey"] = provider == "Fake" ? "pk_test_not_a_secret" : string.Empty,
        };
        if (demoEnabled is not null)
        {
            settings["Demo:Enabled"] = demoEnabled;
        }

        await using var factory = new ApiFactory(Database.ConnectionString) { Settings = settings };
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateApiClient());
        Assert.Contains(missing, error.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>With real Stripe the simulate route doesn't exist, and the config hands out the publishable key.</summary>
[Collection(PostgresCollection.Name)]
public sealed class StripeProviderRouteTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Payments:Provider"] = "Stripe",
        ["Stripe:SecretKey"] = "sk_test_not_a_secret",
        ["Stripe:PublishableKey"] = "pk_test_not_a_secret",
    };

    [Fact]
    public async Task There_is_no_simulate_payment_route_and_the_config_gives_the_publishable_key()
    {
        var response = await NewClient().PostAsync($"/api/v1/demo/payments/{Guid.NewGuid()}/succeed", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // unmatched: the fallback policy asks for sign-in
        Assert.False(SimulateRouteMapped(Factory));

        var config = await NewClient().GetFromJsonAsync<JsonElement>("/api/v1/payments/config", Ct);
        Assert.Equal("stripe", config.GetProperty("provider").GetString());
        Assert.Equal("pk_test_not_a_secret", config.GetProperty("publishableKey").GetString());
    }

    internal static bool SimulateRouteMapped(ApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Any(e => e.RoutePattern.RawText?.Contains("demo/payments", StringComparison.Ordinal) == true);
}

/// <summary>With the demo switched off the simulate route doesn't exist, even with the Fake provider.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoDisabledRouteTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Demo:Enabled"] = "false",
    };

    [Fact]
    public async Task There_is_no_simulate_payment_route()
    {
        var response = await NewClient().PostAsync($"/api/v1/demo/payments/{Guid.NewGuid()}/succeed", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // unmatched: the fallback policy asks for sign-in
        Assert.False(StripeProviderRouteTests.SimulateRouteMapped(Factory));
    }
}
