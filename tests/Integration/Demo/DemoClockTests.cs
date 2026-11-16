using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Demo;
using Reservation.Api.Features.Venues;
using Reservation.Api.Features.Waitlist;
using Reservation.Api.Realtime;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Realtime;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

/// <summary>
/// The presenter's demo clock (M11a, Design §17.2) with the real <see cref="DemoClock"/> as the business clock, counting
/// on an infrastructure <see cref="TestClock"/> (15:00 in Auckland on the seeded evening, so the clock starts at 18:25
/// that evening). Workers off: every step is driven by the test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoClockTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string ClockPath = "/api/v1/demo/clock";
    private const string AdvancePath = "/api/v1/demo/clock/advance";

    private readonly TestClock _infrastructure = new(DemoTime.InfrastructureStart);

    protected override TestClock? InfrastructureClock => _infrastructure;

    protected override bool UseDemoClock => true;

    [Fact]
    public async Task The_clock_starts_fixed_at_18_25_and_closes_at_22_30()
    {
        var presenter = await PresenterAsync();

        var clock = await GetClockAsync(presenter);

        Assert.Equal(DemoTimeMode.Fixed, clock.Mode);
        Assert.Equal(DemoTime.Now.UtcDateTime, clock.At);
        Assert.Equal(new TimeOnly(18, 25), clock.LocalTime);
        Assert.Equal(GoldenDragon.TimeZoneId, clock.TimeZone);
        Assert.Equal(DemoTime.At(22, 30).UtcDateTime, clock.ClosesAt);
    }

    [Fact]
    public async Task Running_moves_business_time_with_real_time_and_freeze_stops_it()
    {
        var presenter = await PresenterAsync();

        var running = await ReadAsync<DemoClockResponse>(await PutAsync(presenter, ClockPath, new { mode = "running" }));
        Assert.Equal((DemoTimeMode.Running, new TimeOnly(18, 25)), (running.Mode, running.LocalTime));

        _infrastructure.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(new TimeOnly(18, 32), (await GetClockAsync(presenter)).LocalTime);
        var plan = await ReadAsync<FloorPlanResponse>(await presenter.GetAsync($"{Venue}/floor-plan", Ct));
        Assert.Equal(DemoTime.At(18, 32).UtcDateTime, plan.At); // the business clock everyone reads

        var frozen = await ReadAsync<DemoClockResponse>(await PutAsync(presenter, ClockPath, new { mode = "fixed" }));
        _infrastructure.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((DemoTimeMode.Fixed, new TimeOnly(18, 32)), (frozen.Mode, (await GetClockAsync(presenter)).LocalTime));
    }

    [Fact]
    public async Task Advance_moves_forward_and_keeps_the_mode()
    {
        var presenter = await PresenterAsync();

        var advanced = await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 15 }));

        Assert.Equal((DemoTimeMode.Fixed, new TimeOnly(18, 40)), (advanced.Mode, advanced.LocalTime));
        Assert.Equal(new TimeOnly(18, 40), (await GetClockAsync(presenter)).LocalTime);
    }

    [Fact]
    public async Task Set_to_a_later_time_moves_there_and_an_earlier_time_is_refused()
    {
        var presenter = await PresenterAsync();

        var later = await ReadAsync<DemoClockResponse>(await PutAsync(presenter, ClockPath, new { mode = "fixed", at = "19:10" }));
        Assert.Equal(new TimeOnly(19, 10), later.LocalTime);

        await ProblemAssert.IsAsync(
            await PutAsync(presenter, ClockPath, new { mode = "fixed", at = "18:40" }), HttpStatusCode.UnprocessableEntity, DemoClockPolicy.Backwards);
        Assert.Equal(new TimeOnly(19, 10), (await GetClockAsync(presenter)).LocalTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    [InlineData(361)]
    public async Task Advancing_backwards_or_by_more_than_6_hours_is_refused(int minutes)
    {
        var presenter = await PresenterAsync();

        var problem = await ProblemAssert.IsAsync(
            await PostAsync(presenter, AdvancePath, new { minutes }), HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(DemoClockPolicy.MinutesOutOfRange, problem.GetProperty("errors").GetProperty("minutes")[0].GetString());
        Assert.Equal(new TimeOnly(18, 25), (await GetClockAsync(presenter)).LocalTime);
    }

    [Fact]
    public async Task Moving_past_closing_time_is_refused()
    {
        var presenter = await PresenterAsync();

        await ProblemAssert.IsAsync(
            await PostAsync(presenter, AdvancePath, new { minutes = 246 }), HttpStatusCode.UnprocessableEntity, DemoClockPolicy.PastClosing);
        await ProblemAssert.IsAsync(
            await PutAsync(presenter, ClockPath, new { mode = "running", at = "22:31" }), HttpStatusCode.UnprocessableEntity, DemoClockPolicy.PastClosing);

        var atClosing = await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 245 }));
        Assert.Equal(new TimeOnly(22, 30), atClosing.LocalTime);
    }

    [Fact]
    public async Task A_missing_mode_is_refused()
    {
        var presenter = await PresenterAsync();

        var problem = await ProblemAssert.IsAsync(
            await PutAsync(presenter, ClockPath, new { at = "19:00" }), HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal("demo_clock_mode_required", problem.GetProperty("errors").GetProperty("mode")[0].GetString());
    }

    [Fact]
    public async Task Reset_puts_the_clock_back_to_fixed_18_25()
    {
        var presenter = await PresenterAsync();
        await PutAsync(presenter, ClockPath, new { mode = "running", at = "19:40" });
        _infrastructure.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(presenter, "/api/v1/demo/reset", new { })).StatusCode);

        var clock = await GetClockAsync(presenter);
        Assert.Equal((DemoTimeMode.Fixed, new TimeOnly(18, 25)), (clock.Mode, clock.LocalTime));
        await using var db = Database.Context();
        var row = await db.DemoClock.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((DemoTimeMode.Fixed, DemoTime.Now), (row.Mode, row.BusinessAt));
    }

    [Fact]
    public async Task The_clock_survives_a_restart()
    {
        var presenter = await PresenterAsync();
        await PutAsync(presenter, ClockPath, new { mode = "fixed", at = "19:05" });

        // A new process: start-up (migrate on) loads the stored state.
        await using var restarted = new ApiFactory(Database.ConnectionString, migrateOnStartup: true)
        {
            InfrastructureClock = new TestClock(_infrastructure.Now.AddMinutes(10)),
            UseDemoClock = true,
        };
        using var client = restarted.CreateApiClient();
        await SignInAsync(client, ApiFactory.PresenterPasscode);

        var clock = await GetClockAsync(client);
        Assert.Equal((DemoTimeMode.Fixed, new TimeOnly(19, 5)), (clock.Mode, clock.LocalTime));
    }

    [Fact]
    public async Task Moving_business_time_never_moves_the_infrastructure_clock_so_payment_holds_keep_real_time()
    {
        var presenter = await PresenterAsync();
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(DemoTime.Tonight, 20, 0, 6, mobile: "021 011 0611")), HttpStatusCode.Created);
        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        var holds = Factory.Services.GetRequiredService<PaymentHoldExpiryJob>();

        await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 60 }));
        Assert.Equal(0, await holds.RunOnceAsync(Ct)); // an hour of business time is not ten minutes of hold

        await using (var db = Database.Context())
        {
            var held = (await db.Bookings.AsNoTracking().ToListAsync(Ct)).Single(b => b.Reference.Value == booking.Reference);
            Assert.Equal(booking.HoldExpiresAt, held.HoldExpiresAt?.UtcDateTime);
        }

        _infrastructure.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await holds.RunOnceAsync(Ct)); // real time still expires it
    }

    [Fact]
    public async Task Hosts_read_the_clock_but_cannot_move_it_and_anonymous_callers_get_nothing()
    {
        var host = NewClient();
        await SignInAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(ClockPath, Ct)).StatusCode);
        await ProblemAssert.IsAsync(await PutAsync(host, ClockPath, new { mode = "running" }), HttpStatusCode.Forbidden, "forbidden");
        await ProblemAssert.IsAsync(await PostAsync(host, AdvancePath, new { minutes = 5 }), HttpStatusCode.Forbidden, "forbidden");

        var anonymous = NewClient();
        await ProblemAssert.IsAsync(await anonymous.GetAsync(ClockPath, Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await ProblemAssert.IsAsync(await PostAsync(anonymous, AdvancePath, new { minutes = 5 }), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    private async Task<HttpClient> PresenterAsync()
    {
        var presenter = NewClient();
        await SignInAsync(presenter, ApiFactory.PresenterPasscode);
        return presenter;
    }

    private static async Task<DemoClockResponse> GetClockAsync(HttpClient client) =>
        await ReadAsync<DemoClockResponse>(await client.GetAsync(ClockPath, Ct));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body) =>
        client.PutAsync(new Uri(path, UriKind.Relative), JsonContent.Create(body, options: ApiJson.Options), Ct);
}

/// <summary>
/// The business-time jobs follow a clock change at once (M11a, AC4.6, AC2.3), with the workers running, the real demo
/// clock as the business clock and the system clock as the infrastructure clock. Each test first resets the demo, which
/// seeds tonight on the demo clock's evening (today) at 18:25.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoClockJobsTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string AdvancePath = "/api/v1/demo/clock/advance";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:RunWorkers"] = "true" };

    protected override bool UseDemoClock => true;

    [Fact]
    public async Task Advancing_11_minutes_makes_a_notified_party_overdue_and_pushes_waitlistChanged_at_once()
    {
        var presenter = await ResetAsync();
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, cookie, Ct);
        await SignInAsync(Client);
        var entry = await ReadAsync<WaitlistItem>(
            await PostAsync(Client, $"{Venue}/waitlist", new { name = "Lee Clock", mobile = "021 011 0161", partySize = 2, quotedWaitMinutes = 10 }),
            HttpStatusCode.Created);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/notify", new { tableId = GoldenDragon.TableId("T1").Value, entry.Version })).StatusCode);

        // Let the notify's own notices (joined, notified, TableReady sent and delivered) arrive first.
        await WaitUntilAsync(async () => (await WaitlistAsync()).Single(e => e.Id == entry.Id).TableReadyStatus == MessageStatus.Delivered);
        await DrainAsync(host, NoticeMethods.WaitlistChanged);
        Assert.False((await WaitlistAsync()).Single(e => e.Id == entry.Id).Overdue);

        var advanced = await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 11 }));
        Assert.Equal(new TimeOnly(18, 36), advanced.LocalTime);

        var notice = await host.NextAsync(NoticeMethods.WaitlistChanged, TimeSpan.FromSeconds(2), Ct);
        Assert.True(notice.HasValue, "No waitlistChanged within 2 s of the clock change.");
        Assert.Equal(entry.Id, Assert.Single(notice.Value.GetProperty("entryIds").EnumerateArray()).GetGuid());
        Assert.True((await WaitlistAsync()).Single(e => e.Id == entry.Id).Overdue);

        var clockNotice = await host.NextAsync(NoticeMethods.DemoClockChanged, TimeSpan.FromSeconds(2), Ct);
        Assert.True(clockNotice.HasValue, "No demoClockChanged.");
        Assert.Equal("fixed", clockNotice.Value.GetProperty("mode").GetString());
        Assert.Equal(["at", "mode"], clockNotice.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Arriving_soon_flips_at_the_30_minute_mark_and_is_pushed_at_once()
    {
        var presenter = await ResetAsync();
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, cookie, Ct);
        await SignInAsync(Client);
        var t6 = GoldenDragon.TableId("T6").Value; // #9 at 19:15
        Assert.Equal(TableState.Booked, await StateAsync("T6"));
        await DrainAsync(host, NoticeMethods.FloorPlanChanged);

        await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 19 })); // 18:44
        Assert.DoesNotContain(t6, await TablesPushedAsync(host, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(TableState.Booked, await StateAsync("T6"));

        await ReadAsync<DemoClockResponse>(await PostAsync(presenter, AdvancePath, new { minutes = 1 })); // 18:45
        Assert.Contains(t6, await TablesPushedAsync(host, TimeSpan.FromSeconds(2), until: t6));
        Assert.Equal(TableState.ArrivingSoon, await StateAsync("T6"));
    }

    private async Task<HttpClient> ResetAsync()
    {
        var presenter = NewClient();
        await SignInAsync(presenter, ApiFactory.PresenterPasscode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(presenter, "/api/v1/demo/reset", new { })).StatusCode);
        return presenter;
    }

    private async Task<IReadOnlyList<WaitlistItem>> WaitlistAsync() =>
        (await ReadAsync<WaitlistResponse>(await Client.GetAsync($"{Venue}/waitlist", Ct))).Items;

    private async Task<TableState> StateAsync(string table) =>
        (await ReadAsync<FloorPlanResponse>(await Client.GetAsync($"{Venue}/floor-plan", Ct))).Tables.Single(t => t.Name == table).State;

    /// <summary>The table IDs in floorPlanChanged notices received within <paramref name="window"/> (or until one names <paramref name="until"/>).</summary>
    private static async Task<HashSet<Guid>> TablesPushedAsync(HubClient host, TimeSpan window, Guid? until = null)
    {
        var tables = new HashSet<Guid>();
        var deadline = DateTime.UtcNow + window;
        for (var remaining = deadline - DateTime.UtcNow; remaining > TimeSpan.Zero; remaining = deadline - DateTime.UtcNow)
        {
            if (await host.NextAsync(NoticeMethods.FloorPlanChanged, remaining, Ct) is not { } notice)
            {
                break;
            }

            tables.UnionWith(notice.GetProperty("tableIds").EnumerateArray().Select(t => t.GetGuid()));
            if (until is { } id && tables.Contains(id))
            {
                break;
            }
        }

        return tables;
    }

    /// <summary>Reads <paramref name="method"/> notices until none arrives for half a second.</summary>
    private static async Task DrainAsync(HubClient host, string method)
    {
        while (await host.NextAsync(method, TimeSpan.FromMilliseconds(500), Ct) is not null)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 5 s.");
            await Task.Delay(100, Ct);
        }
    }
}

/// <summary>The clock routes exist only with the demo on and the demo clock as the business clock.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DemoClockDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Demo:Enabled"] = "false" };

    [Fact]
    public void The_clock_routes_are_not_mapped_with_the_demo_off()
    {
        Assert.DoesNotContain(Routes(Factory), r => r.Contains("demo/clock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_clock_routes_are_not_mapped_with_the_system_clock()
    {
        await using var factory = new ApiFactory(Database.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Demo:Clock:Mode"] = "System" },
        };

        Assert.DoesNotContain(Routes(factory), r => r.Contains("demo/clock", StringComparison.Ordinal));
    }

    private static List<string> Routes(ApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)];
}
