using System.Net;
using Reservation.Api.Features.Bookings;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>GET availability (Design §11, §14) on the seeded evening at 18:25: AC1.1–AC1.3, BR-01 to BR-05.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AvailabilityTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static string Tonight => DemoTime.Tonight.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Party_of_2_at_19_30_is_available_and_only_future_slots_are_offered_AC1_1_BR_01()
    {
        var response = await ReadAsync<AvailabilityResponse>(
            await Client.GetAsync($"{Venue}/availability?date={Tonight}&partySize=2&time=19:30", Ct));

        Assert.True(response.RequestedTimeAvailable);
        Assert.Equal("Pacific/Auckland", response.TimeZone);
        Assert.All(response.Slots, s => Assert.True(s.StartsAt > DemoTime.Now.UtcDateTime));
        Assert.All(response.Slots, s => Assert.Equal(0, s.LocalTime.Minute % 15));
        Assert.Equal(new TimeOnly(18, 30), response.Slots[0].LocalTime);
        Assert.Equal(new TimeOnly(20, 30), response.Slots[^1].LocalTime);
        Assert.DoesNotContain(response.Slots, s => s.LocalTime == new TimeOnly(19, 0)); // pacing full (BR-03)
    }

    [Fact]
    public async Task A_full_time_offers_the_three_nearest_times_AC1_3()
    {
        // 19:00 already has 12 covers (#7 and #8), so a party of 2 would break the pacing limit.
        var response = await ReadAsync<AvailabilityResponse>(
            await Client.GetAsync($"{Venue}/availability?date={Tonight}&partySize=2&time=19:00", Ct));

        Assert.False(response.RequestedTimeAvailable);
        Assert.Equal(
            [new TimeOnly(18, 30), new TimeOnly(18, 45), new TimeOnly(19, 15)],
            response.Slots.Select(s => s.LocalTime));
    }

    [Fact]
    public async Task Only_times_with_a_fitting_free_table_within_pacing_are_offered_AC1_2()
    {
        // The only 8-seat table (T10) is seated until 20:00; at 20:30 the party of 6 (#11) leaves no pacing room.
        var response = await ReadAsync<AvailabilityResponse>(
            await Client.GetAsync($"{Venue}/availability?date={Tonight}&partySize=8", Ct));

        Assert.Null(response.RequestedTimeAvailable);
        Assert.Equal([new TimeOnly(20, 0), new TimeOnly(20, 15)], response.Slots.Select(s => s.LocalTime));
    }

    [Fact]
    public async Task Party_of_6_at_20_00_is_available_for_step_1()
    {
        var response = await ReadAsync<AvailabilityResponse>(
            await Client.GetAsync($"{Venue}/availability?date={Tonight}&partySize=6&time=20:00", Ct));

        Assert.True(response.RequestedTimeAvailable);
    }

    [Fact]
    public async Task Party_above_the_online_limit_is_party_too_large_BR_04()
    {
        var response = await Client.GetAsync($"{Venue}/availability?date={Tonight}&partySize=9", Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "party_too_large");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task A_date_outside_the_booking_window_is_refused_AC1_1(int daysFromToday)
    {
        var date = DemoTime.Tonight.AddDays(daysFromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var response = await Client.GetAsync($"{Venue}/availability?date={date}&partySize=2", Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "outside_booking_window");
    }

    [Fact]
    public async Task The_last_day_of_the_window_is_bookable()
    {
        var date = DemoTime.Tonight.AddDays(30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var response = await ReadAsync<AvailabilityResponse>(await Client.GetAsync($"{Venue}/availability?date={date}&partySize=2", Ct));

        Assert.Equal(15, response.Slots.Count); // 17:00 to 20:30 every 15 minutes
    }

    [Fact]
    public async Task Missing_party_size_and_date_are_validation_failed()
    {
        var response = await Client.GetAsync($"{Venue}/availability", Ct);

        var problem = await ProblemAssert.IsAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.Equal("date_required", errors.GetProperty("date")[0].GetString());
        Assert.Equal("party_size_required", errors.GetProperty("partySize")[0].GetString());
    }

    [Fact]
    public async Task An_unknown_venue_is_not_found()
    {
        var response = await Client.GetAsync($"{OtherVenue}/availability?date={Tonight}&partySize=2", Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.NotFound, "not_found");
    }
}
