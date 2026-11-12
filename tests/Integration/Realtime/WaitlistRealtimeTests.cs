using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Venues;
using Reservation.Api.Features.Waitlist;
using Reservation.Api.Realtime;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Realtime;

/// <summary>
/// AC4.2 through the real pipeline (API → outbox → dispatcher → <see cref="WaitlistSuggester"/> → hub): clearing T4 at
/// 18:25 suggests the waiting party of 4 in <c>waitlistChanged</c>, with IDs only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WaitlistSuggestionDeliveryTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:RunWorkers"] = "true" };

    [Fact]
    public async Task Clearing_T4_suggests_the_waiting_party_of_4_and_names_the_freed_table()
    {
        await SignInAsync(Client);
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, cookie, Ct);
        var lee = await ReadAsync<WaitlistItem>(
            await PostAsync(Client, $"{Venue}/waitlist", new { name = "Lee Suggest", mobile = "021 011 0151", partySize = 4, quotedWaitMinutes = 35 }),
            HttpStatusCode.Created);

        var plan = await ReadAsync<FloorPlanResponse>(await Client.GetAsync($"{Venue}/floor-plan", Ct));
        var current = plan.Tables.Single(t => t.Name == "T4").Current!;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Client, $"{Venue}/tables/{GoldenDragon.TableId("T4").Value}/clear", new { current.BookingId, current.Version })).StatusCode);

        var suggestion = await NextSuggestionAsync(host);
        Assert.True(suggestion.HasValue, "No waitlistChanged with a suggestion within 2 s.");
        var payload = suggestion.Value;
        Assert.Equal(["entryIds", "freedTableId", "suggestedEntryId"], payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(lee.Id, payload.GetProperty("suggestedEntryId").GetGuid());
        Assert.Equal(lee.Id, Assert.Single(payload.GetProperty("entryIds").EnumerateArray()).GetGuid());
        Assert.Equal(GoldenDragon.TableId("T4").Value, payload.GetProperty("freedTableId").GetGuid());
        Assert.DoesNotContain("Lee", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("0151", payload.GetRawText(), StringComparison.Ordinal);

        Assert.NotNull(await host.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromSeconds(2), Ct));
    }

    /// <summary>Skips the joined notice (no suggestion) and returns the first one that carries a suggestion.</summary>
    private async Task<JsonElement?> NextSuggestionAsync(HubClient host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        for (var remaining = deadline - DateTime.UtcNow; remaining > TimeSpan.Zero; remaining = deadline - DateTime.UtcNow)
        {
            var notice = await host.NextAsync(NoticeMethods.WaitlistChanged, remaining, Ct);
            if (notice is { } n && n.GetProperty("suggestedEntryId").ValueKind != JsonValueKind.Null)
            {
                return n;
            }
        }

        return null;
    }
}

/// <summary>Design §16 <c>WaitlistOverdueJob</c> (AC4.6, BR-10), driven one check at a time with the business clock moved.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WaitlistOverdueJobTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task A_notified_party_is_pushed_once_when_it_becomes_overdue()
    {
        await SignInAsync(Client);
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, cookie, Ct);
        var entry = await ReadAsync<WaitlistItem>(
            await PostAsync(Client, $"{Venue}/waitlist", new { name = "Lee Overdue", mobile = "021 011 0152", partySize = 4, quotedWaitMinutes = 35 }),
            HttpStatusCode.Created);
        await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/notify", new { tableId = GoldenDragon.TableId("T4").Value, entry.Version });
        var job = Factory.Services.GetRequiredService<WaitlistOverdueJob>();

        Factory.Clock.Set(DemoTime.At(18, 35));
        Assert.Equal(0, await job.RunOnceAsync(Ct));

        Factory.Clock.Set(DemoTime.At(18, 36));
        Assert.Equal(1, await job.RunOnceAsync(Ct));
        Assert.Equal(0, await job.RunOnceAsync(Ct));

        var notice = await host.NextAsync(NoticeMethods.WaitlistChanged, TimeSpan.FromSeconds(2), Ct);
        Assert.True(notice.HasValue);
        Assert.Equal(entry.Id, Assert.Single(notice.Value.GetProperty("entryIds").EnumerateArray()).GetGuid());
        Assert.Equal(JsonValueKind.Null, notice.Value.GetProperty("suggestedEntryId").ValueKind);
    }
}
