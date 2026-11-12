using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Features.Seating;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Seating;

/// <summary>
/// Design §11.3–§11.5 on <see cref="TestData.Venue"/> (tables of 2, 4, 6 and 8; turns 75 / 90 / 120 minutes):
/// walk-in tables now (BR-05), the quoted wait (BR-08) and the suggested waiting party (BR-09).
/// </summary>
public sealed class SeatingRulesTests
{
    private static readonly DateTimeOffset Now = TestData.Start;
    private static readonly TableId Table4b = new(Guid.Parse("00000000-0000-7000-8000-000000000044"));

    private static readonly IReadOnlyList<TableInfo> Tables =
    [
        new(TestData.Table2, "T2", 2),
        new(TestData.Table4, "T4", 4),
        new(TestData.Table6, "T6", 6),
        new(TestData.Table8, "T8", 8),
    ];

    private static FloorNow Floor(params TableOccupancy[] occupancy) => new(TestData.Settings(), Tables, occupancy, Now);

    private static TableOccupancy Seated(TableId table, int minutesFromNowToEnd, int turnMinutes = 90) =>
        new(BookingId.New(Now), table, new TimeRange(Now.AddMinutes(minutesFromNowToEnd - turnMinutes), Now.AddMinutes(minutesFromNowToEnd)), Seated: true);

    private static TableOccupancy Upcoming(TableId table, int startsInMinutes, int turnMinutes = 90) =>
        new(BookingId.New(Now), table, TimeRange.FromStart(Now.AddMinutes(startsInMinutes), TimeSpan.FromMinutes(turnMinutes)), Seated: false);

    private static WaitingParty Waiting(int partySize, int joinedMinutesAgo) =>
        new(WaitlistEntryId.New(Now.AddMinutes(-joinedMinutesAgo)), partySize, Now.AddMinutes(-joinedMinutesAgo));

    // ---------- FreeTablesNow (AC3.1, AC3.2, BR-05) ----------

    [Fact]
    public void FreeTablesNow_ListsTheFittingFreeTablesSmallestFirst_BR05()
    {
        var free = SeatingRules.FreeTablesNow(Floor(), PartySize.Of(3));

        Assert.Equal([TestData.Table4, TestData.Table6, TestData.Table8], free.Select(t => t.Id));
    }

    [Fact]
    public void FreeTablesNow_EqualSizesKeepFloorOrder()
    {
        var floor = new FloorNow(TestData.Settings(), [new(Table4b, "T4b", 4), new(TestData.Table4, "T4", 4)], [], Now);

        Assert.Equal([Table4b, TestData.Table4], SeatingRules.FreeTablesNow(floor, PartySize.Of(4)).Select(t => t.Id));
    }

    [Fact]
    public void FreeTablesNow_SkipsATableWithASeatedParty_EvenPastItsTurn()
    {
        var free = SeatingRules.FreeTablesNow(Floor(Seated(TestData.Table4, minutesFromNowToEnd: -10)), PartySize.Of(3));

        Assert.DoesNotContain(TestData.Table4, free.Select(t => t.Id));
    }

    [Fact]
    public void FreeTablesNow_SkipsATableBookedWithinTheTurn_BR02()
    {
        // A party of 3 holds a table for 90 minutes: a booking on T4 in 60 minutes clashes, one in 90 minutes doesn't.
        Assert.Equal(TestData.Table6, SeatingRules.FreeTablesNow(Floor(Upcoming(TestData.Table4, 60)), PartySize.Of(3))[0].Id);
        Assert.Equal(TestData.Table4, SeatingRules.FreeTablesNow(Floor(Upcoming(TestData.Table4, 90)), PartySize.Of(3))[0].Id);
    }

    [Fact]
    public void IsFreeNow_IgnoresTheBookingBeingSeated()
    {
        var own = Upcoming(TestData.Table4, 20);

        Assert.False(SeatingRules.IsFreeNow(Floor(own), TestData.Table4, PartySize.Of(4)));
        Assert.True(SeatingRules.IsFreeNow(Floor(own), TestData.Table4, PartySize.Of(4), except: own.BookingId));
    }

    // ---------- QuotedWait (AC3.5, BR-08) ----------

    [Fact]
    public void QuotedWait_WhenAFittingTableIsFreeNow_IsZero_BR08()
    {
        Assert.Equal(0, SeatingRules.QuotedWait(Floor(Seated(TestData.Table4, 30)), PartySize.Of(4)));
    }

    [Fact]
    public void QuotedWait_IsTimeUntilTheEarliestSeatedFittingTableFrees_RoundedUpTo5_BR08()
    {
        var floor = Floor(Seated(TestData.Table6, 33), Seated(TestData.Table8, 50));

        Assert.Equal(35, SeatingRules.QuotedWait(floor, PartySize.Of(6)));
    }

    [Fact]
    public void QuotedWait_WhenASeatedPartyIsPastItsTurn_IsZero()
    {
        var floor = Floor(Seated(TestData.Table8, -5));

        Assert.Equal(0, SeatingRules.QuotedWait(floor, PartySize.Of(8)));
    }

    [Fact]
    public void QuotedWait_WhenOnlyUpcomingBookingsBlock_UsesTheEarliestEnd()
    {
        var floor = Floor(Upcoming(TestData.Table8, 10, turnMinutes: 120));

        Assert.Equal(130, SeatingRules.QuotedWait(floor, PartySize.Of(8)));
    }

    [Fact]
    public void QuotedWait_IsCappedAt240Minutes()
    {
        var floor = Floor(Seated(TestData.Table8, 300, turnMinutes: 320));

        Assert.Equal(240, SeatingRules.QuotedWait(floor, PartySize.Of(8)));
    }

    [Fact]
    public void QuotedWait_WhenNoTableSeatsTheParty_IsNull()
    {
        Assert.Null(SeatingRules.QuotedWait(Floor(), PartySize.Of(9)));
    }

    // ---------- SuggestEntry (AC4.2, BR-09) ----------

    [Fact]
    public void SuggestEntry_IsTheFirstPartyByJoinTimeThatTheTableSeats_BR09()
    {
        var six = Waiting(6, joinedMinutesAgo: 30);
        var four = Waiting(4, joinedMinutesAgo: 20);
        var two = Waiting(2, joinedMinutesAgo: 25);

        var suggested = SeatingRules.SuggestEntry(Floor(), Tables[1], [four, six, two]);

        Assert.Equal(two.Id, suggested);
    }

    [Fact]
    public void SuggestEntry_SkipsAPartyWhoseTurnClashesWithTheNextBooking()
    {
        // T4 is booked in 80 minutes: a party of 4 (90 minutes) doesn't fit, a party of 2 (75 minutes) does.
        var four = Waiting(4, joinedMinutesAgo: 30);
        var two = Waiting(2, joinedMinutesAgo: 10);

        var suggested = SeatingRules.SuggestEntry(Floor(Upcoming(TestData.Table4, 80)), Tables[1], [four, two]);

        Assert.Equal(two.Id, suggested);
    }

    [Fact]
    public void SuggestEntry_WhenNoPartyFits_IsNull()
    {
        Assert.Null(SeatingRules.SuggestEntry(Floor(), Tables[0], [Waiting(4, 10), Waiting(6, 5)]));
    }
}
