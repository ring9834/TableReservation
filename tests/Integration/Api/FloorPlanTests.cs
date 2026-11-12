using System.Net;
using Reservation.Api.Features.Venues;
using Reservation.Domain.Bookings;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>GET floor-plan (Design §9.3, §15.1): the layout and each table's state at business-clock "now" (§2.5).</summary>
[Collection(PostgresCollection.Name)]
public sealed class FloorPlanTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly string FloorPlan = $"{Venue}/floor-plan";

    [Fact]
    public async Task Seed_at_18_25_gives_the_expected_state_for_every_table()
    {
        await SignInAsync(Client);

        var plan = await ReadAsync<FloorPlanResponse>(await Client.GetAsync(FloorPlan, Ct));

        Assert.Equal(GoldenDragon.VenueId.Value, plan.VenueId);
        Assert.Equal("Pacific/Auckland", plan.TimeZone);
        Assert.Equal(DemoTime.Now.UtcDateTime, plan.At);
        Assert.Equal([GoldenDragon.MainRoomId.Value, GoldenDragon.WindowId.Value], plan.Areas.Select(a => a.Id));
        Assert.Equal(["Main room", "Window"], plan.Areas.Select(a => a.Name));
        Assert.Equal(["主厅", "窗边"], plan.Areas.Select(a => a.NameZh)); // M6a: the screen picks by language
        Assert.Equal(GoldenDragon.Tables.Select(t => t.Name), plan.Tables.Select(t => t.Name));

        // Design §17 seed table at 18:25.
        var expected = new Dictionary<string, TableState>
        {
            ["T1"] = TableState.Free,
            ["T2"] = TableState.Free,
            ["T3"] = TableState.Free,
            ["T4"] = TableState.Seated,       // #1 17:30
            ["T5"] = TableState.ArrivingSoon, // #6 18:45, within 30 min
            ["T6"] = TableState.Booked,       // #9 19:15
            ["T7"] = TableState.Booked,       // #7 19:00, 35 min away
            ["T8"] = TableState.Booked,       // #8 19:00
            ["T9"] = TableState.Seated,       // #2 17:30, then #11 20:30
            ["T10"] = TableState.Seated,      // #4 18:00
            ["W1"] = TableState.Booked,       // #10 20:30
            ["W2"] = TableState.Free,
            ["W3"] = TableState.Seated,       // #3 17:45
            ["W4"] = TableState.Seated,       // #5 18:15
        };
        Assert.Equal(expected, plan.Tables.ToDictionary(t => t.Name, t => t.State));

        var free = plan.Tables.Where(t => t.State == TableState.Free);
        Assert.All(free, t => Assert.Null(t.Current));
        Assert.All(free, t => Assert.Null(t.Next));
    }

    [Fact]
    public async Task Tables_carry_layout_and_a_summary_of_the_current_and_next_booking()
    {
        await SignInAsync(Client);

        var plan = await ReadAsync<FloorPlanResponse>(await Client.GetAsync(FloorPlan, Ct));
        var tables = plan.Tables.ToDictionary(t => t.Name);

        foreach (var spec in GoldenDragon.Tables)
        {
            var table = tables[spec.Name];
            Assert.Equal(GoldenDragon.TableId(spec.Name).Value, table.Id);
            Assert.Equal(spec.Area.Value, table.AreaId);
            Assert.Equal(spec.Seats, table.Seats);
            Assert.Equal(spec.Position.Shape, table.Shape);
            Assert.Equal(new TablePositionDto(spec.Position.X, spec.Position.Y, spec.Position.Width, spec.Position.Height, spec.Position.Rotation), table.Position);
        }

        // T9: #2 seated now, #11 still to come tonight.
        var t9 = tables["T9"];
        Assert.Equal("GD3B7M", t9.Current!.Reference);
        Assert.Equal(BookingStatus.Seated, t9.Current.Status);
        Assert.Equal(5, t9.Current.PartySize);
        Assert.Equal(DemoTime.At(17, 30).UtcDateTime, t9.Current.StartsAt);
        Assert.Equal(GoldenDragon.Guests[1].Name, t9.Current.GuestName);
        Assert.Equal("GDC4L8", t9.Next!.Reference);
        Assert.Equal(DemoTime.At(20, 30).UtcDateTime, t9.Next.StartsAt);

        // T5: nobody seated, #6 arriving at 18:45. The full stored name, family name first included.
        var t5 = tables["T5"];
        Assert.Null(t5.Current);
        Assert.Equal("GD7F3R", t5.Next!.Reference);
        Assert.Equal(BookingStatus.Confirmed, t5.Next.Status);
        Assert.Equal(4, t5.Next.PartySize);
        Assert.Equal(GoldenDragon.Guests[5].Name, t5.Next.GuestName);

        var t4 = tables["T4"];
        Assert.Equal("GD2A4K", t4.Current!.Reference);
        Assert.Equal("王伟 Wang Wei", t4.Current.GuestName);
        Assert.Null(t4.Next);
    }

    [Fact]
    public async Task A_new_booking_shows_on_its_table()
    {
        await SignInAsync(Client);
        var created = await PostAsync(Client, $"{Venue}/bookings", BookingBody(DemoTime.Tonight, 19, 30, 2));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var plan = await ReadAsync<FloorPlanResponse>(await Client.GetAsync(FloorPlan, Ct));

        var t1 = plan.Tables.Single(t => t.Name == "T1");
        Assert.Equal(TableState.Booked, t1.State);
        Assert.Equal("Test Guest", t1.Next!.GuestName);
        Assert.Equal(DemoTime.At(19, 30).UtcDateTime, t1.Next.StartsAt);
    }

    [Fact]
    public async Task The_response_has_no_mobile_numbers_or_notes()
    {
        await SignInAsync(Client);

        var body = await (await Client.GetAsync(FloorPlan, Ct)).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("+64", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mobile", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anonymous_is_401_and_another_venue_is_403()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync(FloorPlan, Ct), HttpStatusCode.Unauthorized, "not_signed_in");

        await SignInAsync(Client);
        await ProblemAssert.IsAsync(await Client.GetAsync($"{OtherVenue}/floor-plan", Ct), HttpStatusCode.Forbidden, "forbidden");
    }
}
