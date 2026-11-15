using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// <c>GET …/pos-messages</c> and <c>GET …/pos/status</c> (AC6.1, AC6.2, Design §6.7): newest first, filtered, phone
/// numbers masked, presenter only; the demo reset; the per-venue sequence under concurrency.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PosLogTests(PostgresFixture postgres) : PosTestBase(postgres)
{
    [Fact]
    public async Task The_log_lists_both_directions_newest_first_with_the_phone_masked()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Aroha Ngata", "021 011 0291");
        await SeatAsync(booking.BookingId);
        await DispatchAsync();
        await DeliverPosAsync();
        await BillPaidAsync("T1", 4_200);

        var presenter = await PresenterClientAsync();
        var log = await ReadAsync<JsonElement>(await presenter.GetAsync($"{Venue}/pos-messages", Ct));

        Assert.Equal("Pacific/Auckland", log.GetProperty("timeZone").GetString());
        var items = log.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["BillClosed", "OpenTable", "UpsertMember"], items.Select(i => i.GetProperty("messageType").GetString()));
        Assert.Equal([3L, 2L, 1L], items.Select(i => i.GetProperty("venueSequence").GetInt64()));
        Assert.Equal(["inbound", "outbound", "outbound"], items.Select(i => i.GetProperty("direction").GetString()));
        Assert.Equal(["matched", "sent", "sent"], items.Select(i => i.GetProperty("status").GetString()));

        var member = items[2];
        Assert.Equal(booking.BookingId, member.GetProperty("bookingId").GetGuid());
        Assert.Equal(0, member.GetProperty("tableIds").GetArrayLength());
        Assert.Equal(1, member.GetProperty("attempts").GetInt32());
        Assert.Equal(DemoTime.Now, member.GetProperty("deliveredAt").GetDateTimeOffset());
        Assert.Equal("+64******291", member.GetProperty("payload").GetProperty("memberPhone").GetString());
        Assert.Equal("Aroha Ngata", member.GetProperty("payload").GetProperty("name").GetString());
        Assert.DoesNotContain("0110291", log.GetRawText(), StringComparison.Ordinal);
        Assert.Equal([Table("T1")], items[1].GetProperty("tableIds").EnumerateArray().Select(t => t.GetGuid()));

        await using var db = Database.Context();
        var stored = await db.PosMessages.AsNoTracking().SingleAsync(m => m.MessageType == "UpsertMember", Ct);
        Assert.Equal("+64210110291", stored.Payload.RootElement.GetProperty("memberPhone").GetString()); // the POS gets it unmasked
    }

    [Fact]
    public async Task The_log_filters_by_status()
    {
        await BillPaidAsync("T3", 1_000); // unmatched
        await BillPaidAsync("T4", 1_000); // matched

        var presenter = await PresenterClientAsync();
        var unmatched = await ReadAsync<JsonElement>(await presenter.GetAsync($"{Venue}/pos-messages?status=unmatched", Ct));
        var item = Assert.Single(unmatched.GetProperty("items").EnumerateArray());
        Assert.Equal("no_match", item.GetProperty("unmatchedReason").GetString());

        var invalid = await presenter.GetAsync($"{Venue}/pos-messages?status=lost", Ct);
        await ProblemAssert.IsAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task The_log_is_for_presenters_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync($"{Venue}/pos-messages", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().GetAsync($"{Venue}/pos-messages", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync($"{Venue}/pos/status", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PutAsJsonAsync("/api/v1/demo/pos/online", new { online = false }, Ct)).StatusCode);

        var presenter = await PresenterClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await presenter.GetAsync($"{OtherVenue}/pos-messages", Ct)).StatusCode);
        var status = await ReadAsync<JsonElement>(await presenter.GetAsync($"{Venue}/pos/status", Ct));
        Assert.True(status.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task The_demo_reset_clears_the_POS_tables_and_puts_the_mock_online()
    {
        await SetOnlineAsync(false);
        await WalkInAsync("T1");
        await DispatchAsync();
        await BillPaidAsync("T4", 1_000);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoReset>().ResetAsync(Ct);
        }

        await using var db = Database.Context();
        Assert.Empty(await db.PosMessages.ToListAsync(Ct));
        Assert.Empty(await db.PosVenueSequences.ToListAsync(Ct));
        Assert.Empty(await db.MockPosState.ToListAsync(Ct));
        Assert.Empty(await db.MockPosReceipts.ToListAsync(Ct));
        Assert.Empty(await db.MockPosOpenTables.ToListAsync(Ct));
        Assert.True(await Factory.Services.GetRequiredService<MockPosAdapter>().IsOnlineAsync(Ct));
    }

    [Fact]
    public async Task Parallel_allocations_give_unique_gap_free_sequences_and_a_rollback_leaves_no_gap()
    {
        const int Writers = 20;
        var allocated = await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(AllocateAsync)));
        Assert.Equal(Enumerable.Range(1, Writers).Select(i => (long)i), allocated.Order());

        await using (var db = Database.Context())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.Equal(Writers + 1, await new PosSequenceAllocator(db).NextAsync(GoldenDragon.VenueId, Ct));
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(Writers + 1, await AllocateAsync());

        async Task<long> AllocateAsync()
        {
            await using var db = Database.Context();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var next = await new PosSequenceAllocator(db).NextAsync(GoldenDragon.VenueId, Ct);
            await Task.Delay(5, Ct); // hold the row lock a moment, as a handler would
            await transaction.CommitAsync(Ct);
            return next;
        }
    }

    [Fact]
    public async Task Two_dispatchers_projecting_at_once_give_gap_free_sequences_in_commit_order()
    {
        foreach (var table in new[] { "T1", "T2", "T3", "W1", "W2" })
        {
            await WalkInAsync(table);
        }

        var first = Factory.Services.GetRequiredService<OutboxDispatcher>();
        var second = new OutboxDispatcher(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<OutboxSignal>(),
            Factory.Services.GetRequiredService<IOptions<OutboxOptions>>(),
            Infra,
            Factory.Services.GetRequiredService<ILogger<OutboxDispatcher>>());
        await Task.WhenAll(
            Task.Run(async () => await first.DispatchBatchAsync(Ct)),
            Task.Run(async () => await second.DispatchBatchAsync(Ct)));
        await DispatchAsync();

        var rows = await PosRowsAsync();
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (long)i), rows.Select(r => r.VenueSequence));
    }
}

/// <summary>With the demo switched off the mock POS routes don't exist; the POS log and the webhook do.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PosDemoDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Demo:Enabled"] = "false",
        ["Pos:WebhookSecret"] = string.Empty,
    };

    [Fact]
    public void The_mock_POS_routes_are_not_mapped()
    {
        var routes = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(routes, r => r.Contains("demo/pos", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, r => r.Contains("pos/status", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("pos-messages", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("webhooks/pos", StringComparison.Ordinal));
    }
}

/// <summary>With the demo on, the POS webhook secret is required at start-up.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PosStartupTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Start_up_without_Pos_WebhookSecret_fails_naming_it()
    {
        await using var factory = new ApiFactory(Database.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Pos:WebhookSecret"] = string.Empty },
        };

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateApiClient());
        Assert.Contains("Pos:WebhookSecret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_secret_every_POS_webhook_is_refused()
    {
        await using var factory = new ApiFactory(Database.ConnectionString)
        {
            Settings = new Dictionary<string, string?> { ["Demo:Enabled"] = "false", ["Pos:WebhookSecret"] = string.Empty },
        };
        using var client = factory.CreateApiClient();
        const string Body = """{"messageType":"BillClosed"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, PosTestBase.WebhookPath) { Content = new StringContent(Body) };
        request.Headers.Add(PosWebhookSigner.Header, PosWebhookSigner.Sign(Body, "anything", DateTimeOffset.UtcNow));

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemAssert.IsAsync(response, HttpStatusCode.Unauthorized, "invalid_signature");
    }
}
