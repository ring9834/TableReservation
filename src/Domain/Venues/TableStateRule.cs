using Reservation.Domain.Bookings;
using Reservation.Domain.Common;

namespace Reservation.Domain.Venues;

/// <summary>A table's state on the floor plan; derived, never stored (§2.4, §2.5).</summary>
public enum TableState
{
    Free,
    Booked,
    ArrivingSoon,
    Seated,
}

/// <summary>The parts of a booking the table-state rule needs.</summary>
public sealed record TableBookingView(BookingStatus Status, TimeRange Period);

/// <summary>Design §2.5: derives a table's state from the bookings assigned to it.</summary>
public static class TableStateRule
{
    /// <summary>How far ahead a booking makes its table "arriving soon" (AC2.3).</summary>
    public static readonly TimeSpan ArrivingSoonWindow = TimeSpan.FromMinutes(30);

    /// <param name="bookings">Bookings assigned to this table.</param>
    /// <param name="now">Current time.</param>
    /// <param name="endOfServiceDay">End of today's service day in venue time, as an instant.</param>
    public static TableState Derive(IEnumerable<TableBookingView> bookings, DateTimeOffset now, DateTimeOffset endOfServiceDay)
    {
        ArgumentNullException.ThrowIfNull(bookings);
        var list = bookings.ToList();

        if (list.Any(b => b.Status == BookingStatus.Seated))
        {
            return TableState.Seated;
        }

        var upcoming = list
            .Where(b => b.Status is BookingStatus.PendingPayment or BookingStatus.Booked or BookingStatus.Confirmed)
            .Where(b => b.Period.End > now)
            .ToList();

        if (upcoming.Any(b => b.Period.Start <= now + ArrivingSoonWindow))
        {
            return TableState.ArrivingSoon;
        }

        if (upcoming.Any(b => b.Period.Start < endOfServiceDay))
        {
            return TableState.Booked;
        }

        return TableState.Free;
    }
}
