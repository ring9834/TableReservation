using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>
/// M8a (Design §8.4): the payment hold on the infrastructure clock, the attached payment intent, and payments that arrive
/// after the hold expiry cancelled the booking.
/// </summary>
public sealed class BookingPaymentTests
{
    /// <summary>The infrastructure clock in these tests: far from the business clock, as when the demo clock is fixed.</summary>
    private static readonly DateTimeOffset _real = TestData.Created.AddDays(3);

    private static Booking PendingOnRealTime() =>
        Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.ContactName, TestData.Reference(), BookingChannel.Online, PartySize.Of(6),
            TestData.Start, [TestData.Table8], null, null, TestData.Created, holdClockNow: _real).Value;

    // ---------- Hold on the infrastructure clock ----------

    [Fact]
    public void Create_MeasuresTheHoldFromTheInfrastructureClock()
    {
        var booking = PendingOnRealTime();

        Assert.Equal(_real.AddMinutes(10), booking.HoldExpiresAt);
        Assert.Equal(TestData.Created, booking.CreatedAt);
    }

    [Fact]
    public void ExpireHold_ComparesTheHoldWithTheInfrastructureClock_AndStampsTheBusinessClock()
    {
        var booking = PendingOnRealTime();
        var businessNow = TestData.Created; // fixed

        EventAssert.InvalidTransition(booking.ExpireHold(businessNow, _real.AddMinutes(9)));
        var before = booking.LastEventSequence;
        Assert.True(booking.ExpireHold(businessNow, _real.AddMinutes(10)).IsSuccess);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(businessNow, EventAssert.RaisedOnce<BookingCancelled>(booking, before).OccurredAt);
    }

    [Fact]
    public void CaptureDeposit_ComparesTheHoldWithTheInfrastructureClock()
    {
        var inTime = PendingOnRealTime();
        var late = PendingOnRealTime();

        // The business clock is days "early", yet the hold has expired in real time and the table is taken: cancelled.
        Assert.True(inTime.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: false, TestData.Created, _real.AddMinutes(9)).IsSuccess);
        Assert.True(late.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: false, TestData.Created, _real.AddMinutes(11)).IsSuccess);

        Assert.Equal(BookingStatus.Booked, inTime.Status);
        Assert.Equal(BookingStatus.Cancelled, late.Status);
        Assert.Equal(RefundDecision.Full, late.CancellationRefund);
    }

    // ---------- AttachPaymentIntent ----------

    [Fact]
    public void AttachPaymentIntent_StoresTheIntentOnce_WithoutAnEvent()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        Assert.True(booking.AttachPaymentIntent("pi_1").IsSuccess);
        Assert.True(booking.AttachPaymentIntent("pi_1").IsSuccess);
        EventAssert.Failed(booking.AttachPaymentIntent("pi_2"), ErrorCodes.PaymentIntentMismatch, ErrorKind.Rule);

        Assert.Equal("pi_1", booking.Deposit!.PaymentIntentId);
        Assert.Equal(DepositStatus.Pending, booking.Deposit.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void AttachPaymentIntent_WithoutADeposit_IsInvalidTransition() =>
        EventAssert.InvalidTransition(BookingFactory.Create().AttachPaymentIntent("pi_1"));

    [Fact]
    public void CaptureDeposit_ForAnotherIntentThanTheAttachedOne_IsRefused()
    {
        var booking = BookingFactory.PendingPayment();
        booking.AttachPaymentIntent("pi_1");
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_other", Money.FromNzd(12_000), true, TestData.Created.AddMinutes(1));

        EventAssert.Failed(result, ErrorCodes.PaymentIntentMismatch, ErrorKind.Rule);
        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- RecordLatePayment ----------

    [Fact]
    public void RecordLatePayment_AfterTheHoldExpiry_KeepsTheBookingCancelled_AndRaisesLatePaymentReceived()
    {
        var booking = ExpiredWithIntent();
        var before = booking.LastEventSequence;

        var result = booking.RecordLatePayment("pi_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(12));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelReason.HoldExpired, booking.CancellationReason);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        var late = EventAssert.RaisedOnce<LatePaymentReceived>(booking, before);
        Assert.Equal("pi_1", late.PaymentIntentId);
        Assert.Equal(12_000, late.AmountCents);
    }

    [Fact]
    public void RecordLatePayment_Again_IsNoOp()
    {
        var booking = ExpiredWithIntent();
        booking.RecordLatePayment("pi_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(12));
        var before = booking.LastEventSequence;

        Assert.True(booking.RecordLatePayment("pi_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(13)).IsSuccess);

        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void RecordLatePayment_ThenChargeRefunded_RecordsTheRefund()
    {
        var booking = ExpiredWithIntent();
        booking.RecordLatePayment("pi_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(12));
        var before = booking.LastEventSequence;

        Assert.True(booking.RecordRefund("re_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(20)).IsSuccess);

        Assert.Equal(DepositStatus.Refunded, booking.Deposit!.Status);
        EventAssert.RaisedOnce<DepositRefunded>(booking, before);
    }

    [Fact]
    public void RecordLatePayment_WithAWrongAmountOrIntent_IsRefused()
    {
        var booking = ExpiredWithIntent();
        var before = booking.LastEventSequence;

        EventAssert.Failed(booking.RecordLatePayment("pi_1", Money.FromNzd(100), TestData.Created), ErrorCodes.DepositAmountMismatch, ErrorKind.Rule);
        EventAssert.Failed(booking.RecordLatePayment("pi_2", Money.FromNzd(12_000), TestData.Created), ErrorCodes.PaymentIntentMismatch, ErrorKind.Rule);

        Assert.Equal(DepositStatus.Released, booking.Deposit!.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Cancelled)] // cancelled by the guest, not by the hold expiry
    public void RecordLatePayment_UnlessCancelledByTheHoldExpiry_IsInvalidTransition(BookingStatus status)
    {
        var booking = status == BookingStatus.Booked ? BookingFactory.BookedWithDeposit() : BookingFactory.InStatus(status);

        EventAssert.InvalidTransition(booking.RecordLatePayment("pi_1", Money.FromNzd(12_000), TestData.Created.AddHours(1)));
    }

    private static Booking ExpiredWithIntent()
    {
        var booking = BookingFactory.PendingPayment();
        booking.AttachPaymentIntent("pi_1");
        Assert.True(booking.ExpireHold(TestData.Created.AddMinutes(10)).IsSuccess);
        return booking;
    }
}
