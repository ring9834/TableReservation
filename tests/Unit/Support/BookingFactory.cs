using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Tests.Unit.Support;

/// <summary>Builds bookings in each status by calling the aggregate's own methods.</summary>
public static class BookingFactory
{
    public const string PaymentIntent = "pi_test_1";

    public static Booking Create(
        int partySize = 2,
        BookingChannel channel = BookingChannel.Online,
        DateTimeOffset? start = null,
        DateTimeOffset? now = null,
        Venue? venue = null) =>
        Booking.Create(
            venue ?? TestData.Venue(), TestData.GuestId, TestData.Reference(), channel, PartySize.Of(partySize),
            start ?? TestData.Start, [partySize <= 4 ? TestData.Table4 : TestData.Table8], null, null, now ?? TestData.Created).Value;

    /// <summary>Party of 6: deposit NZD 120 due, 10-minute hold.</summary>
    public static Booking PendingPayment(DateTimeOffset? now = null) => Create(partySize: 6, now: now);

    /// <summary>Party of 6 with the deposit captured inside the hold.</summary>
    public static Booking BookedWithDeposit()
    {
        var booking = PendingPayment();
        booking.CaptureDeposit(PaymentIntent, booking.Deposit!.Amount, tableStillFree: true, TestData.Created.AddMinutes(2));
        return booking;
    }

    public static Booking InStatus(BookingStatus status)
    {
        var booking = status == BookingStatus.PendingPayment ? PendingPayment() : Create();
        switch (status)
        {
            case BookingStatus.Confirmed:
                booking.Confirm(TestData.GuestActor, TestData.Created.AddHours(1));
                break;
            case BookingStatus.Seated:
                booking.Seat([TestData.Table4], TestData.Start, TestData.Host);
                break;
            case BookingStatus.Completed:
                booking.Seat([TestData.Table4], TestData.Start, TestData.Host);
                booking.Complete(CompletionSource.ClearedByHost, null, TestData.Start.AddMinutes(70));
                break;
            case BookingStatus.Cancelled:
                booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Created.AddHours(1));
                break;
            case BookingStatus.PendingPayment:
            case BookingStatus.Booked:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, booking.Status);
        return booking;
    }

    /// <summary>Seated, then cleared by the host at <see cref="TestData.Start"/> + 70 minutes, with no bill.</summary>
    public static Booking ClearedByHost() => InStatus(BookingStatus.Completed);

    public static DateTimeOffset ClearedAt => TestData.Start.AddMinutes(70);
}
