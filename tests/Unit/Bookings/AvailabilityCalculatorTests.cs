using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>Design §11.2 on <see cref="TestData.Venue"/> (tables of 2, 4, 6 and 8), Friday 20 November 2026.</summary>
public sealed class AvailabilityCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
    private static readonly IReadOnlyList<TableInfo> Tables =
    [
        new(TestData.Table2, "T2", 2),
        new(TestData.Table4, "T4", 4),
        new(TestData.Table6, "T6", 6),
        new(TestData.Table8, "T8", 8),
    ];

    private static DateTimeOffset At(int hour, int minute) => AvailabilityCalculator.ToUtc(Day, new TimeOnly(hour, minute), Auckland);

    private static AvailabilityInput Input(
        IReadOnlyList<BusyPeriod>? busy = null, IReadOnlyList<BookedCovers>? covers = null, DateTimeOffset? now = null) =>
        new(TestData.Settings(), Auckland, Tables, busy ?? [], covers ?? [], now ?? TestData.Created);

    private static IEnumerable<TimeOnly> Times(AvailabilitySearch search) => search.Slots.Select(s => s.LocalTime);

    [Fact]
    public void Slots_RunEvery15MinutesFromServiceStartToLastBooking_BR01()
    {
        var search = AvailabilityCalculator.Search(Input(), Day, PartySize.Of(2), null);

        Assert.Equal(15, search.Slots.Count);
        Assert.Equal(new TimeOnly(17, 0), search.Slots[0].LocalTime);
        Assert.Equal(new TimeOnly(20, 30), search.Slots[^1].LocalTime);
        Assert.Null(search.RequestedTimeAvailable);
    }

    [Fact]
    public void Slots_AtOrBeforeNow_AreNotOffered()
    {
        var search = AvailabilityCalculator.Search(Input(now: At(19, 0)), Day, PartySize.Of(2), null);

        Assert.Equal(new TimeOnly(19, 15), search.Slots[0].LocalTime);
    }

    [Fact]
    public void Evaluate_PicksTheSmallestTableThatFits_BR05()
    {
        Assert.Equal(TestData.Table2, AvailabilityCalculator.Evaluate(Input(), At(19, 0), PartySize.Of(1))!.Table.Id);
        Assert.Equal(TestData.Table4, AvailabilityCalculator.Evaluate(Input(), At(19, 0), PartySize.Of(3))!.Table.Id);
        Assert.Equal(TestData.Table8, AvailabilityCalculator.Evaluate(Input(), At(19, 0), PartySize.Of(7))!.Table.Id);
    }

    [Fact]
    public void Evaluate_SkipsATableBusyAtAnyPointOfTheTurn_BR02()
    {
        // A party of 2 at 19:00 holds the table for 75 minutes; a booking on T2 from 20:00 overlaps it.
        var busy = new BusyPeriod(TestData.Table2, TimeRange.FromStart(At(20, 0), TimeSpan.FromMinutes(75)));

        var slot = AvailabilityCalculator.Evaluate(Input([busy]), At(19, 0), PartySize.Of(2));

        Assert.Equal(TestData.Table4, slot!.Table.Id);
    }

    [Fact]
    public void Evaluate_ATableFreedExactlyAtTheStart_IsFree()
    {
        var busy = new BusyPeriod(TestData.Table2, new TimeRange(At(17, 45), At(19, 0)));

        Assert.Equal(TestData.Table2, AvailabilityCalculator.Evaluate(Input([busy]), At(19, 0), PartySize.Of(2))!.Table.Id);
    }

    [Fact]
    public void Evaluate_NoFittingFreeTable_ReturnsNull()
    {
        var busy = new BusyPeriod(TestData.Table8, TimeRange.FromStart(At(18, 0), TimeSpan.FromMinutes(180)));

        Assert.Null(AvailabilityCalculator.Evaluate(Input([busy]), At(19, 0), PartySize.Of(8)));
    }

    [Theory]
    [InlineData(10, 2, true)] // 10 + 2 = 12, at the limit
    [InlineData(10, 3, false)] // 13 > 12
    public void Evaluate_KeepsCoversStartingInTheSlotWithinThePacingLimit_BR03(int booked, int party, bool available)
    {
        var covers = new[] { new BookedCovers(At(19, 0), booked - 1), new BookedCovers(At(19, 10), 1), new BookedCovers(At(19, 15), 12) };

        var slot = AvailabilityCalculator.Evaluate(Input(covers: covers), At(19, 0), PartySize.Of(party));

        Assert.Equal(available, slot is not null);
    }

    [Fact]
    public void Search_RequestedTimeAvailable_ReturnsEverySlot()
    {
        var search = AvailabilityCalculator.Search(Input(), Day, PartySize.Of(2), new TimeOnly(19, 30));

        Assert.True(search.RequestedTimeAvailable);
        Assert.Equal(15, search.Slots.Count);
    }

    [Fact]
    public void Search_RequestedTimeFull_ReturnsTheThreeNearestWithEarlierFirstOnTies_AC13()
    {
        // 19:00 is full. 18:45 and 19:15 are 15 minutes away; 18:30 and 19:30 tie at 30, and the earlier one wins.
        var covers = new[] { new BookedCovers(At(19, 0), 12) };

        var search = AvailabilityCalculator.Search(Input(covers: covers), Day, PartySize.Of(2), new TimeOnly(19, 0));

        Assert.False(search.RequestedTimeAvailable);
        Assert.Equal([new TimeOnly(18, 30), new TimeOnly(18, 45), new TimeOnly(19, 15)], Times(search));
    }

    [Fact]
    public void Search_RequestedTimeOffTheGrid_ReturnsTheNearestSlots()
    {
        var search = AvailabilityCalculator.Search(Input(), Day, PartySize.Of(2), new TimeOnly(19, 7));

        Assert.False(search.RequestedTimeAvailable);
        Assert.Equal([new TimeOnly(18, 45), new TimeOnly(19, 0), new TimeOnly(19, 15)], Times(search));
    }

    [Fact]
    public void SlotsOn_DaylightSavingStartDay_StillGivesLocalServiceTimes()
    {
        // NZ daylight saving starts on Sunday 27 September 2026 at 02:00; the evening is at UTC+13.
        var day = new DateOnly(2026, 9, 27);

        var slots = AvailabilityCalculator.SlotsOn(TestData.Settings(), Auckland, day);

        Assert.Equal(15, slots.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 4, 0, 0, TimeSpan.Zero), slots[0].StartsAt);
    }

    [Fact]
    public void ServiceWindow_CoversTheLastSlotPlusTheLongestTurn()
    {
        var window = AvailabilityCalculator.ServiceWindow(TestData.Settings(), Auckland, Day);

        Assert.Equal(At(17, 0), window.Start);
        Assert.Equal(At(22, 30), window.End); // 20:30 + 120 minutes
    }
}
