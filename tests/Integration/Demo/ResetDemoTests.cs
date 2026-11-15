using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Realtime;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.GuestLinks;
using Reservation.Tests.Integration.Realtime;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

/// <summary>
/// <c>POST /demo/reset</c> (M10b, Design §17, §18.10): after a demo run (reminders sent, #11 cancelled by its guest,
/// the mock POS offline) the presenter's reset brings back the seeded evening and tells every screen.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResetDemoTests(PostgresFixture postgres) : GuestLinkTestBase(postgres)
{
    private const string ResetPath = "/api/v1/demo/reset";

    [Fact]
    public async Task Reset_after_a_demo_run_brings_back_the_seeded_evening_and_tells_the_screens()
    {
        using var presenter = await PresenterAsync();
        await SendRemindersAsync();
        await ActAsync("cancel", await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel"));
        await DispatchAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await presenter.PutAsync(new Uri("/api/v1/demo/pos/online", UriKind.Relative), Json(new { online = false }), Ct)).StatusCode);
        var fakeRefunds = Factory.Services.GetRequiredService<FakeRefundQueue>();
        fakeRefunds.Add("pi_seed_waiting", 12_000, "NZD", "re_fake_waiting");

        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var hub = await HubClient.ConnectAsync(Factory, cookie, Ct);

        var response = await PostAsync(presenter, ResetPath, new { });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using (var db = Database.Context())
        {
            var bookings = await db.Bookings.AsNoTracking().ToListAsync(Ct);
            Assert.Equal(GoldenDragon.Bookings.Select(b => b.Reference).Order(), bookings.Select(b => b.Reference.Value).Order());
            Assert.All(bookings, b => Assert.Null(b.Reminder));
            Assert.Equal(GoldenDragon.Guests.Count, await db.Guests.CountAsync(Ct));
            Assert.Equal(0, await db.Messages.CountAsync(Ct));
            Assert.Equal(0, await db.Outbox.CountAsync(Ct));
            Assert.Equal(0, await db.Inbox.CountAsync(Ct));
            Assert.Equal(0, await db.PosMessages.CountAsync(Ct));
        }

        var cancelled = await ByReferenceAsync(CancelsRef);
        Assert.Equal((BookingStatus.Booked, DepositStatus.Captured), (cancelled.Status, cancelled.Deposit!.Status));
        Assert.Equal(0, fakeRefunds.Count);
        Assert.True((await ReadAsync<PosStatus>(await presenter.GetAsync($"{Venue}/pos/status", Ct))).Online);

        var notice = await hub.NextAsync(NoticeMethods.DemoReset, TimeSpan.FromSeconds(2), Ct);
        Assert.True(notice.HasValue, "No demoReset within 2 s.");
        Assert.Empty(notice.Value.EnumerateObject());

        // The seeded step 5 works again.
        Assert.Equal(2, (await SendRemindersAsync()).Sent);
    }

    [Fact]
    public async Task Hosts_and_anonymous_callers_cannot_reset()
    {
        var host = NewClient();
        await SignInAsync(host);

        await ProblemAssert.IsAsync(await PostAsync(host, ResetPath, new { }), HttpStatusCode.Forbidden, "forbidden");
        await ProblemAssert.IsAsync(await PostAsync(NewClient(), ResetPath, new { }), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    [Fact]
    public async Task A_worker_batch_that_never_finishes_makes_the_reset_answer_503_demo_reset_busy_and_change_nothing()
    {
        using var presenter = await PresenterAsync();
        await SendRemindersAsync();
        var pause = Factory.Services.GetRequiredService<WorkerPause>();

        using (await pause.EnterAsync(Ct))
        {
            await ProblemAssert.IsAsync(await PostAsync(presenter, ResetPath, new { }), HttpStatusCode.ServiceUnavailable, "demo_reset_busy");
        }

        Assert.NotNull((await ByReferenceAsync(ConfirmsRef)).Reminder); // nothing was reset
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(presenter, ResetPath, new { })).StatusCode); // and it works once free
        Assert.Null((await ByReferenceAsync(ConfirmsRef)).Reminder);
    }

    private static StringContent Json(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private sealed record PosStatus(bool Online);
}

/// <summary>With the demo switched off, the reset isn't mapped.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ResetDemoDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Demo:Enabled"] = "false" };

    [Fact]
    public void The_reset_route_is_not_mapped()
    {
        var routes = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(routes, r => r.Contains("demo/reset", StringComparison.Ordinal));
    }
}

/// <summary>
/// The reset with every worker RUNNING (outbox dispatcher, message worker, POS worker, hold expiry, Fake refund
/// reporter; M10b fix, Design §18.10): each round leaves confirmations, OpenTable messages, refunds and reminders in
/// flight, keeps booking while the reset runs, and still gets 204 and the seeded evening, five times over. Before the
/// fix a deadlock between the reset's TRUNCATE and a worker's batch came back as 409 <c>slot_taken</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ResetDemoWithWorkersTests(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    private const int Rounds = 5;

    // Lots of bookings and sign-ins from one test client: lift the per-IP limits for this test only.
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Outbox:RunWorkers"] = "true",
        ["RateLimits:AnonymousPerMinute"] = "10000",
        ["RateLimits:BookingPerMinute"] = "10000",
        ["RateLimits:DemoLoginPerMinute"] = "100",
    };

    [Fact]
    public async Task Reset_returns_204_and_the_seed_while_work_is_in_flight_every_time()
    {
        await SignInAsync(Client);
        using var presenter = await PresenterAsync();

        for (var round = 1; round <= Rounds; round++)
        {
            await MakeWorkAsync(presenter, round);

            // More bookings arrive while the reset runs; they may win or lose, the reset must not.
            using var stop = new CancellationTokenSource();
            var traffic = KeepBookingAsync(round, stop.Token);
            var reset = await presenter.PostAsync(new Uri("/api/v1/demo/reset", UriKind.Relative), null, Ct);
            await stop.CancelAsync();
            await traffic;

            Assert.True(
                reset.StatusCode == HttpStatusCode.NoContent,
                $"Round {round}: reset answered {(int)reset.StatusCode} {await reset.Content.ReadAsStringAsync(Ct)}");
            // Tonight is the seed again (the traffic books tomorrow, so some of it may land after the reset): the walk-ins
            // are gone, #6 is confirmed again, #7 and #11 booked with their deposits, and no reminders.
            await using var db = Database.Context();
            var tomorrow = GoldenDragon.At(Tomorrow, TimeOnly.MinValue);
            var tonight = (await db.Bookings.AsNoTracking().ToListAsync(Ct)).Where(b => b.Period.Start < tomorrow).ToList();
            Assert.Equal(
                GoldenDragon.Bookings.Select(b => (b.Reference, b.Status.ToString())).Order(),
                tonight.Select(b => (b.Reference.Value, b.Status.ToString())).Order());
            Assert.All(tonight, b => Assert.Null(b.Reminder));
            Assert.All(tonight.Where(b => b.Deposit is not null), b => Assert.Equal(DepositStatus.Captured, b.Deposit!.Status));
            Assert.Equal(0, await db.WaitlistEntries.CountAsync(Ct));
        }

        // The workers carry on afterwards: a new booking's confirmation is delivered.
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 18, 0, 2, mobile: "021 011 0599")), HttpStatusCode.Created);
        var deadline = Stopwatch.StartNew();
        while ((await MessagesAboutAsync(booking.BookingId)).SingleOrDefault()?.Status != MessageStatus.Delivered)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), "The workers did not deliver a confirmation after the resets.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Confirmations, OpenTable messages (walk-ins and seating #6), refunds (#7 and #11) and a reminder (#10).</summary>
    private async Task MakeWorkAsync(HttpClient presenter, int round)
    {
        for (var i = 0; i < 3; i++)
        {
            await ReadAsync<CreateBookingResponse>(
                await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 18 + i, 0, 2, mobile: $"021 011 05{round}{i}")),
                HttpStatusCode.Created);
        }

        foreach (var table in new[] { "T1", "T2", "W2" })
        {
            await ReadAsync<JsonElement>(
                await PostAsync(Client, $"{Venue}/walk-ins", new { partySize = 2, tableId = Table(table) }), HttpStatusCode.Created);
        }

        var tonight = (await ReadAsync<JsonElement>(await Client.GetAsync($"{Venue}/bookings", Ct))).GetProperty("items").EnumerateArray().ToList();
        foreach (var item in tonight)
        {
            var id = item.GetProperty("id").GetGuid();
            var version = item.GetProperty("version").GetUInt32();
            var status = item.GetProperty("status").GetString();
            var paid = item.TryGetProperty("depositStatus", out var deposit) && deposit.ValueKind == JsonValueKind.String && deposit.GetString() == "captured";
            if (status == "confirmed")
            {
                Assert.True((await PostAsync(Client, $"{Venue}/bookings/{id}/seat", new { version })).IsSuccessStatusCode);
            }
            else if (status == "booked" && paid)
            {
                Assert.True((await PostAsync(Client, $"{Venue}/bookings/{id}/cancel", new { version })).IsSuccessStatusCode);
            }
        }

        Assert.True((await PostAsync(presenter, "/api/v1/demo/reminders/send-now", new { })).IsSuccessStatusCode);
    }

    private async Task KeepBookingAsync(int round, CancellationToken stop)
    {
        var client = NewClient();
        for (var i = 0; !stop.IsCancellationRequested && i < 8; i++)
        {
            try
            {
                using var response = await PostAsync(
                    client, $"{Venue}/bookings", BookingBody(Tomorrow, 18 + (i % 4), 30, 2, mobile: $"021 012 {round}{i:00}0"));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
