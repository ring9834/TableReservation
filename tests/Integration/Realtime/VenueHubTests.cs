using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reservation.Api.Realtime;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Realtime;

/// <summary>Design §15, §15.1: who may connect, which group a connection joins, and that a push failure is harmless.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VenueHubTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    protected override void ConfigureServices(IServiceCollection services) => TestEndpoints.Add(services);

    [Fact]
    public async Task An_anonymous_connection_is_rejected()
    {
        await using var anonymous = HubClient.Create(Factory, cookie: null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.Connection.StartAsync(Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task A_host_joins_only_its_own_venue_group()
    {
        await using var goldenDragon = await HubClient.ConnectAsync(Factory, await HostCookieAsync(), Ct);
        await using var otherVenue = await HubClient.ConnectAsync(Factory, await OtherVenueCookieAsync(), Ct);
        var hub = Factory.Services.GetRequiredService<IHubContext<VenueHub>>();

        await hub.Clients.Group(VenueGroups.For(GoldenDragon.VenueId)).SendAsync(NoticeMethods.FloorPlanChanged, new FloorPlanChangedNotice([], [], 1), Ct);
        Assert.NotNull(await goldenDragon.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromSeconds(5), Ct));
        Assert.Null(await otherVenue.NextAsync(NoticeMethods.FloorPlanChanged, Quiet, Ct));

        await hub.Clients.Group(VenueGroups.For(TestEndpoints.OtherVenueId)).SendAsync(NoticeMethods.FloorPlanChanged, new FloorPlanChangedNotice([], [], 2), Ct);
        Assert.NotNull(await otherVenue.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromSeconds(5), Ct));
        Assert.Null(await goldenDragon.NextAsync(NoticeMethods.FloorPlanChanged, Quiet, Ct));
    }

    [Fact]
    public async Task A_presenter_session_may_connect_too()
    {
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.PresenterPasscode }, Ct);

        await using var presenter = await HubClient.ConnectAsync(Factory, cookie, Ct);

        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, presenter.Connection.State);
    }

    [Fact]
    public async Task A_throwing_push_is_logged_and_the_outbox_row_is_still_dispatched()
    {
        await using var throwing = new ApiFactory(Database.ConnectionString)
        {
            ExtraServices = services => services.AddSingleton<IVenueBroadcaster, ThrowingBroadcaster>(),
        };
        using var client = throwing.CreateApiClient();
        var created = await PostAsync(client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Equal(1, await throwing.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Ct));

        await using var db = Database.Context();
        var row = await db.Outbox.SingleAsync(Ct);
        Assert.Equal(OutboxStatus.Dispatched, row.Status);
        Assert.Null(row.LastError);
        Assert.Equal(0, row.Attempts);
        var warning = Assert.Single(throwing.Logs.Lines, l => l.Category.Contains("RealtimeNotifier", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("BookingCreated", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingBroadcaster.Secret, warning.Text, StringComparison.Ordinal);
    }

    private Task<string> HostCookieAsync() =>
        HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);

    private Task<string> OtherVenueCookieAsync() =>
        HubClient.SignInAsync(Factory, "/api/v1/test/sign-in/other-venue", null, Ct);

    private sealed class ThrowingBroadcaster : IVenueBroadcaster
    {
        public const string Secret = "push-failed-detail";

        public Task SendAsync(VenueId venueId, RealtimeNotice notice, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);
    }
}

/// <summary>QA-1 through the real pipeline: API → outbox → dispatcher (signalled on commit) → hub → client.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RealtimeDeliveryTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:RunWorkers"] = "true" };

    protected override void ConfigureServices(IServiceCollection services) => TestEndpoints.Add(services);

    [Fact]
    public async Task A_booking_reaches_a_connected_host_within_2_seconds_with_ids_only()
    {
        var hostCookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        var otherCookie = await HubClient.SignInAsync(Factory, "/api/v1/test/sign-in/other-venue", null, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, hostCookie, Ct);
        await using var otherVenue = await HubClient.ConnectAsync(Factory, otherCookie, Ct);

        var stopwatch = Stopwatch.StartNew();
        var response = await PostAsync(
            Client, $"{Venue}/bookings",
            BookingBody(DemoTime.Tonight, 19, 30, 2, name: "Ana Realtime", mobile: "021 011 0177", notes: "window please", allergyNotes: "peanuts"));
        var created = await ReadAsync<JsonElement>(response, HttpStatusCode.Created);
        var notice = await host.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromSeconds(2), Ct);
        stopwatch.Stop();

        Assert.True(notice.HasValue, $"No floorPlanChanged within 2 s (waited {stopwatch.ElapsedMilliseconds} ms).");
        var payload = notice.Value;
        Assert.Equal(["bookingIds", "sequence", "tableIds"], payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(created.GetProperty("bookingId").GetGuid(), Assert.Single(payload.GetProperty("bookingIds").EnumerateArray()).GetGuid());
        Assert.Equal(GoldenDragon.TableId("T1").Value, Assert.Single(payload.GetProperty("tableIds").EnumerateArray()).GetGuid());
        Assert.Equal(1, payload.GetProperty("sequence").GetInt64());

        var raw = payload.GetRawText();
        Assert.DoesNotContain("Ana", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("0177", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("window", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("peanuts", raw, StringComparison.Ordinal);

        Assert.Null(await otherVenue.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromMilliseconds(500), Ct));
    }
}
