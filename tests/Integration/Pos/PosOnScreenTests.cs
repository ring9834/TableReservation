using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Venues;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// The M9b additions the screens read (Design §18.9): table names on POS log items, the floor plan's
/// <c>demoEnabled</c>, and the guest's visits and last bill on tonight's list (AC6.6).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PosOnScreenTests(PostgresFixture postgres) : PosTestBase(postgres)
{
    [Fact]
    public async Task Log_items_name_their_tables_and_UpsertMember_names_none()
    {
        var booking = await PhoneBookingAsync(19, 30, 2, "Aroha Ngata", "021 011 0292");
        await SeatAsync(booking.BookingId);
        await DispatchAsync();
        await BillPaidAsync("T1", 4_200);

        var presenter = await PresenterClientAsync();
        var log = await ReadAsync<JsonElement>(await presenter.GetAsync($"{Venue}/pos-messages", Ct));

        var names = log.GetProperty("items").EnumerateArray().ToDictionary(
            i => i.GetProperty("messageType").GetString()!,
            i => i.GetProperty("tableNames").EnumerateArray().Select(n => n.GetString()).ToList());
        Assert.Equal(["T1"], names["OpenTable"]);
        Assert.Equal(["T1"], names["BillClosed"]);
        Assert.Empty(names["UpsertMember"]);
    }

    [Fact]
    public async Task The_floor_plan_says_the_demo_controls_exist()
    {
        var plan = await ReadAsync<JsonElement>(await Client.GetAsync($"{Venue}/floor-plan", Ct));

        Assert.True(plan.GetProperty("demoEnabled").GetBoolean());
    }

    [Fact]
    public async Task Tonights_list_shows_the_guests_visits_and_last_bill_after_Bill_paid()
    {
        const string Mobile = "021 011 0293";
        var first = await PhoneBookingAsync(19, 30, 2, "Mere Parata", Mobile);
        var later = await PhoneBookingAsync(20, 0, 2, "Mere Parata", Mobile);
        var walkIn = await WalkInAsync("T2");

        var before = await ListAsync();
        Assert.Equal(0, before[later.Reference].GetProperty("visitCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, before[later.Reference].GetProperty("lastBillTotal").ValueKind);
        Assert.Equal(JsonValueKind.Null, before[walkIn.Reference].GetProperty("visitCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, before[walkIn.Reference].GetProperty("lastBillTotal").ValueKind);

        await SeatAsync(first.BookingId);
        await DispatchAsync();
        await BillPaidAsync("T1", 12_345);
        await DispatchAsync();

        var after = await ListAsync();
        var history = after[later.Reference];
        Assert.Equal(1, history.GetProperty("visitCount").GetInt32());
        Assert.Equal(12_345, history.GetProperty("lastBillTotal").GetProperty("amountCents").GetInt64());
        Assert.Equal("NZD", history.GetProperty("lastBillTotal").GetProperty("currency").GetString());
        Assert.Equal(1, after[first.Reference].GetProperty("visitCount").GetInt32()); // same guest, same profile
    }

    private async Task<Dictionary<string, JsonElement>> ListAsync()
    {
        var list = await ReadAsync<JsonElement>(await Client.GetAsync($"{Venue}/bookings", Ct));
        return list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("reference").GetString()!);
    }
}

/// <summary>With the demo switched off the floor plan says so, and the host screen hides "Bill paid" (M9b).</summary>
[Collection(PostgresCollection.Name)]
public sealed class FloorPlanDemoDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Demo:Enabled"] = "false",
        ["Pos:WebhookSecret"] = string.Empty,
    };

    [Fact]
    public async Task DemoEnabled_is_false()
    {
        // /demo-login isn't mapped without the demo, so the handler is asked directly, in the venue as a request sets it.
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<VenueContext>().Set(GoldenDragon.VenueId);
        var handler = scope.ServiceProvider.GetRequiredService<GetFloorPlanHandler>();

        var plan = await handler.Handle(new GetFloorPlan(GoldenDragon.VenueId), TestContext.Current.CancellationToken);

        Assert.True(plan.IsSuccess);
        Assert.False(plan.Value.DemoEnabled);
    }
}
