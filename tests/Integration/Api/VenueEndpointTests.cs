using System.Net;
using Reservation.Api.Features.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>GET venue (Design §14, §18.4): what the guest page needs before it asks for availability.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VenueEndpointTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Golden_Dragon_returns_its_slot_grid_limits_and_contact_phone_BR_01_BR_04()
    {
        var venue = await ReadAsync<VenueResponse>(await Client.GetAsync(Venue, Ct));

        Assert.Equal(GoldenDragon.VenueId.Value, venue.VenueId);
        Assert.Equal("Golden Dragon", venue.Name);
        Assert.Equal("Pacific/Auckland", venue.TimeZone);
        Assert.Equal(DemoTime.Tonight, venue.Today); // venue-local date on the business clock (18:25)
        Assert.Equal(15, venue.SlotTimes.Count);
        Assert.Equal(new TimeOnly(17, 0), venue.SlotTimes[0]);
        Assert.Equal(new TimeOnly(20, 30), venue.SlotTimes[^1]);
        Assert.All(venue.SlotTimes, t => Assert.Equal(0, t.Minute % 15));
        Assert.Equal(8, venue.MaxOnlinePartySize);
        Assert.Equal(30, venue.BookingWindowDays);
        Assert.Equal(GoldenDragon.ContactPhone, venue.ContactPhone);
    }

    [Fact]
    public async Task Full_times_are_still_listed_so_the_guest_can_choose_one_AC1_3()
    {
        // 19:00 is full at pacing (seed #7 and #8) but stays on the grid; availability then offers the nearest times.
        var venue = await ReadAsync<VenueResponse>(await Client.GetAsync(Venue, Ct));

        Assert.Contains(new TimeOnly(19, 0), venue.SlotTimes);
    }

    [Fact]
    public async Task Unknown_venue_is_not_found()
    {
        var response = await Client.GetAsync(OtherVenue, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.NotFound, "not_found");
    }
}
