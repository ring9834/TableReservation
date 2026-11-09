using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>RefundPolicy (BR-07, §8.3).</summary>
public class RefundPolicyTests
{
    [Fact]
    public void Decide_WhenNoDeposit_IsNotApplicable()
    {
        var booking = BookingFactory.Create();

        Assert.Equal(RefundDecision.NotApplicable,
            RefundPolicy.Decide(booking, CancelReason.HostRequest, TestData.Host, TestData.Created));
    }

    [Fact]
    public void Decide_WhenDepositStillPending_IsNotApplicable()
    {
        var booking = BookingFactory.PendingPayment();

        Assert.Equal(RefundDecision.NotApplicable,
            RefundPolicy.Decide(booking, CancelReason.HoldExpired, Actor.ForSystem(), TestData.Created.AddMinutes(10)));
    }

    [Theory]
    [InlineData(-180, RefundDecision.Full)]
    [InlineData(-120, RefundDecision.Full)]
    [InlineData(-119, RefundDecision.None)]
    [InlineData(-5, RefundDecision.None)]
    public void Decide_ForGuest_FullRefundOnlyTwoHoursOrMoreBefore_BR07(int minutesFromStart, RefundDecision expected)
    {
        var booking = BookingFactory.BookedWithDeposit();

        Assert.Equal(expected,
            RefundPolicy.Decide(booking, CancelReason.GuestRequest, TestData.GuestActor, TestData.Start.AddMinutes(minutesFromStart)));
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(-1)]
    public void Decide_ForHost_AlwaysFull_BR07(int minutesFromStart)
    {
        var booking = BookingFactory.BookedWithDeposit();

        Assert.Equal(RefundDecision.Full,
            RefundPolicy.Decide(booking, CancelReason.HostRequest, TestData.Host, TestData.Start.AddMinutes(minutesFromStart)));
    }

    [Fact]
    public void Decide_WhenHoldExpiredAfterPayment_IsFull()
    {
        var booking = BookingFactory.BookedWithDeposit();

        Assert.Equal(RefundDecision.Full,
            RefundPolicy.Decide(booking, CancelReason.HoldExpired, Actor.ForSystem(), TestData.Start.AddMinutes(-5)));
    }

    [Fact]
    public void Decide_UsesTheBookingsTermsNotTheVenueDefaults()
    {
        var venue = TestData.Venue();
        venue.UpdateSettings(venue.Settings with
        {
            Cancellation = new CancellationRule(24, RefundAmount.Full, RefundAmount.None),
        });
        var booking = BookingFactory.Create(partySize: 6, venue: venue);
        booking.CaptureDeposit("pi_1", booking.Deposit!.Amount, true, TestData.Created.AddMinutes(1));

        Assert.Equal(RefundDecision.None,
            RefundPolicy.Decide(booking, CancelReason.GuestRequest, TestData.GuestActor, TestData.Start.AddHours(-3)));
        Assert.Equal(RefundDecision.None,
            RefundPolicy.Decide(booking, CancelReason.HostRequest, TestData.Host, TestData.Start.AddHours(-3)));
    }
}

/// <summary>DepositRule (BR-06, §8.2).</summary>
public class DepositRuleTests
{
    [Theory]
    [InlineData(1, null)]
    [InlineData(5, null)]
    [InlineData(6, 12_000L)]
    [InlineData(8, 16_000L)]
    [InlineData(20, 40_000L)]
    public void DueFor_ChargesTwentyDollarsPerGuestFromSix_BR06(int partySize, long? expectedCents)
    {
        var rule = new DepositRule(6, 2000);

        var due = rule.DueFor(PartySize.Of(partySize), Money.Nzd);

        Assert.Equal(expectedCents, due?.Cents);
        if (due is { } money)
        {
            Assert.Equal(Money.Nzd, money.Currency);
        }
    }
}

/// <summary>ReminderPolicy (BR-13, §7.1).</summary>
public class ReminderPolicyTests
{
    [Fact]
    public void CanSend_WhenBookedMadeEarlyAndStartsLater_IsTrue()
    {
        var booking = BookingFactory.Create();

        Assert.True(ReminderPolicy.CanSend(booking, TestData.Start.AddHours(-24)));
    }

    [Theory]
    [InlineData(24 * 60, true)]
    [InlineData(24 * 60 - 1, false)]
    public void CanSend_DependsOnHowFarAheadTheBookingWasMade_BR13(int minutesAhead, bool expected)
    {
        var booking = BookingFactory.Create(now: TestData.Start.AddMinutes(-minutesAhead));

        Assert.Equal(expected, ReminderPolicy.CanSend(booking, TestData.Start.AddHours(-3)));
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(119, false)]
    [InlineData(0, false)]
    public void CanSend_NeverWithinTwoHoursOfStart_BR13(int minutesBeforeStart, bool expected)
    {
        var booking = BookingFactory.Create();

        Assert.Equal(expected, ReminderPolicy.CanSend(booking, TestData.Start.AddMinutes(-minutesBeforeStart)));
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public void CanSend_OnlyForUnconfirmedBookedBookings_BR13(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);

        Assert.False(ReminderPolicy.CanSend(booking, TestData.Start.AddHours(-24)));
    }
}
