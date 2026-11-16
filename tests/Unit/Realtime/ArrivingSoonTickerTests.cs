using Reservation.Api.Realtime;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Tests.Unit.Realtime;

/// <summary>The arriving-soon ticker's pure parts (AC2.3, M11a, Design §16).</summary>
public sealed class ArrivingSoonTickerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 5, 25, 0, TimeSpan.Zero); // 18:25 Auckland
    private static readonly DateTimeOffset EndOfDay = Now.AddHours(6);
    private static readonly TableId T5 = new(Guid.Parse("00000000-0000-0000-0000-000000000005"));
    private static readonly TableId T6 = new(Guid.Parse("00000000-0000-0000-0000-000000000006"));

    private static TableBookingView Booking(int startsInMinutes, BookingStatus status = BookingStatus.Booked) =>
        new(status, TimeRange.FromStart(Now.AddMinutes(startsInMinutes), TimeSpan.FromMinutes(90)));

    [Fact]
    public void ArrivingSoon_IsWithin30Minutes()
    {
        var tables = ArrivingSoonTicker.ArrivingSoon([(T5, Booking(20)), (T6, Booking(50))], Now, EndOfDay);

        Assert.Equal([T5], tables);
    }

    [Fact]
    public void ArrivingSoon_FlipsAtThe30MinuteMark()
    {
        Assert.Empty(ArrivingSoonTicker.ArrivingSoon([(T6, Booking(31))], Now, EndOfDay));
        Assert.Equal([T6], ArrivingSoonTicker.ArrivingSoon([(T6, Booking(30))], Now, EndOfDay));
    }

    [Fact]
    public void ASeatedTable_IsNotArrivingSoon()
    {
        var tables = ArrivingSoonTicker.ArrivingSoon(
            [(T5, Booking(-30, BookingStatus.Seated)), (T5, Booking(20))], Now, EndOfDay);

        Assert.Empty(tables);
    }

    [Fact]
    public void Changed_IsTheSymmetricDifference()
    {
        Assert.Equal([T5, T6], ArrivingSoonTicker.Changed(new HashSet<TableId> { T5 }, new HashSet<TableId> { T6 }));
        Assert.Empty(ArrivingSoonTicker.Changed(new HashSet<TableId> { T5 }, new HashSet<TableId> { T5 }));
    }
}
