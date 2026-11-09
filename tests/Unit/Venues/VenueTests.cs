using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Venues;

/// <summary>Venue aggregate: tables, settings, turn times (BR-02) and slots (BR-01).</summary>
public class VenueTests
{
    [Fact]
    public void Create_WithValidInput_HasTablesAndVersionOne()
    {
        var venue = TestData.Venue();

        Assert.Equal("Golden Dragon", venue.Name);
        Assert.Equal(1, venue.SettingsVersion);
        Assert.Equal(4, venue.Tables.Count());
        Assert.Equal(6, venue.FindTable(TestData.Table6)!.Seats);
        Assert.Null(venue.FindTable(new TableId(Guid.NewGuid())));
        EventAssert.NoneRaised(venue, 0);
    }

    [Fact]
    public void Create_WithUnknownTimeZone_ReturnsValidationFailed()
    {
        var result = Venue.Create(new VenueId(Guid.NewGuid()), "X", "Mars/Olympus", Money.Nzd, TestData.Settings(), [Area()]);

        EventAssert.Failed(result, ErrorCodes.ValidationFailed, ErrorKind.Validation);
    }

    [Fact]
    public void Create_WithDuplicateTableIds_ReturnsValidationFailed()
    {
        var position = new FloorPosition(0, 0, 0, TableShape.Rect, 1, 1);
        var area = new Area(new AreaId(Guid.NewGuid()), "Main", 1,
            [new Table(TestData.Table2, "A", 2, position), new Table(TestData.Table2, "B", 2, position)]);

        var result = Venue.Create(new VenueId(Guid.NewGuid()), "X", "Pacific/Auckland", Money.Nzd, TestData.Settings(), [area]);

        EventAssert.Failed(result, ErrorCodes.ValidationFailed, ErrorKind.Validation);
    }

    [Fact]
    public void UpdateSettings_IncreasesSettingsVersion()
    {
        var venue = TestData.Venue();

        var result = venue.UpdateSettings(venue.Settings with { LateBillMatchMinutes = 45 });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, venue.SettingsVersion);
        Assert.Equal(45, venue.Settings.LateBillMatchMinutes);
    }

    [Fact]
    public void UpdateSettings_WithInvalidSettings_ReturnsValidationFailedAndKeepsVersion()
    {
        var venue = TestData.Venue();

        var result = venue.UpdateSettings(venue.Settings with { TurnTimes = [] });

        EventAssert.Failed(result, ErrorCodes.ValidationFailed, ErrorKind.Validation);
        Assert.Equal(1, venue.SettingsVersion);
    }

    [Theory]
    [InlineData(1, 75)]
    [InlineData(2, 75)]
    [InlineData(3, 90)]
    [InlineData(4, 90)]
    [InlineData(5, 120)]
    [InlineData(8, 120)]
    [InlineData(12, 120)]
    [InlineData(20, 120)]
    public void TurnMinutesFor_FollowsPartySize_BR02(int partySize, int minutes)
    {
        Assert.Equal(minutes, TestData.Settings().TurnMinutesFor(PartySize.Of(partySize)));
    }

    [Fact]
    public void IsBookableSlot_UsesVenueTimeAcrossDaylightSaving_BR01()
    {
        var settings = TestData.Settings();
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

        // 19:00 NZST (UTC+12) in June and 19:00 NZDT (UTC+13) in November are both on the grid.
        Assert.True(settings.IsBookableSlot(new DateTimeOffset(2026, 6, 10, 7, 0, 0, TimeSpan.Zero), auckland));
        Assert.True(settings.IsBookableSlot(new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero), auckland));
        // 06:00 UTC in June is 18:00 NZST: still a slot; 05:59 is off the grid.
        Assert.False(settings.IsBookableSlot(new DateTimeOffset(2026, 6, 10, 5, 59, 0, TimeSpan.Zero), auckland));
    }

    [Fact]
    public void Table_Fits_WhenSeatsAtLeastPartySize()
    {
        var table = TestData.Venue().FindTable(TestData.Table4)!;

        Assert.True(table.Fits(PartySize.Of(4)));
        Assert.False(table.Fits(PartySize.Of(5)));
    }

    private static Area Area() =>
        new(new AreaId(Guid.NewGuid()), "Main", 1, [new Table(TestData.Table2, "T2", 2, new FloorPosition(0, 0, 0, TableShape.Round, 1, 1))]);
}

/// <summary>Table state is derived from bookings (Design §2.5).</summary>
public class TableStateRuleTests
{
    private static readonly DateTimeOffset _now = TestData.Start.AddHours(-1);
    private static readonly DateTimeOffset _endOfDay = TestData.Start.AddHours(5);

    [Fact]
    public void Derive_WithNoBookings_IsFree()
    {
        Assert.Equal(TableState.Free, TableStateRule.Derive([], _now, _endOfDay));
    }

    [Fact]
    public void Derive_WithSeatedBooking_IsSeatedFirst()
    {
        TableBookingView[] bookings =
        [
            new(BookingStatus.Booked, Period(_now.AddMinutes(10))),
            new(BookingStatus.Seated, Period(_now.AddMinutes(-30))),
        ];

        Assert.Equal(TableState.Seated, TableStateRule.Derive(bookings, _now, _endOfDay));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    public void Derive_WhenUpcomingBookingStartsWithin30Minutes_IsArrivingSoon(BookingStatus status)
    {
        Assert.Equal(TableState.ArrivingSoon, TableStateRule.Derive([new(status, Period(_now.AddMinutes(30)))], _now, _endOfDay));
    }

    [Fact]
    public void Derive_WhenUpcomingBookingStartsLaterToday_IsBooked()
    {
        Assert.Equal(TableState.Booked, TableStateRule.Derive([new(BookingStatus.Booked, Period(_now.AddMinutes(31)))], _now, _endOfDay));
    }

    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public void Derive_WithOnlyEndedBookings_IsFree(BookingStatus status)
    {
        Assert.Equal(TableState.Free, TableStateRule.Derive([new(status, Period(_now.AddMinutes(10)))], _now, _endOfDay));
    }

    [Fact]
    public void Derive_WhenBookingIsTomorrow_IsFree()
    {
        Assert.Equal(TableState.Free, TableStateRule.Derive([new(BookingStatus.Booked, Period(_endOfDay.AddHours(1)))], _now, _endOfDay));
    }

    private static TimeRange Period(DateTimeOffset start) => TimeRange.FromStart(start, TimeSpan.FromMinutes(90));
}
