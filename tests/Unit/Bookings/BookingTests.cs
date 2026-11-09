using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>Booking aggregate: every row of Design §3.1, the rejected transitions, BR-02/04/06/07/13.</summary>
public class BookingTests
{
    private readonly TestClock _clock = new(TestData.Created);

    // ---------- Create (→ Booked, → PendingPayment) ----------

    [Fact]
    public void Create_WhenNoDepositDue_IsBookedAndRaisesBookingCreated()
    {
        var booking = BookingFactory.Create(partySize: 2, now: _clock.Now);

        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Null(booking.Deposit);
        Assert.Null(booking.HoldExpiresAt);
        Assert.Equal(new TimeRange(TestData.Start, TestData.Start.AddMinutes(75)), booking.Period);
        Assert.Equal(_clock.Now, booking.CreatedAt);
        var table = Assert.Single(booking.Tables);
        Assert.Equal(TestData.Table4, table.TableId);
        Assert.True(table.Active);

        var created = EventAssert.RaisedOnce<BookingCreated>(booking, 0);
        Assert.Equal(booking.Id, created.BookingId);
        Assert.Equal(booking.VenueId, created.VenueId);
        Assert.Equal("GD7K4P", created.Reference);
        Assert.Equal(BookingChannel.Online, created.Channel);
        Assert.Equal(2, created.PartySize);
        Assert.Equal(TestData.Start, created.StartsAt);
        Assert.Equal(TestData.Start.AddMinutes(75), created.EndsAt);
        Assert.Equal([TestData.Table4], created.TableIds);
        Assert.Null(created.DepositCents);
        Assert.Null(created.HoldExpiresAt);
        Assert.Equal(_clock.Now, created.OccurredAt);
    }

    [Fact]
    public void Create_WhenPartyOfSix_IsPendingPaymentWithDepositAndHold()
    {
        var booking = BookingFactory.Create(partySize: 6, now: _clock.Now);

        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        Assert.NotNull(booking.Deposit);
        Assert.Equal(Money.FromNzd(12_000), booking.Deposit.Amount);
        Assert.Equal(DepositStatus.Pending, booking.Deposit.Status);
        Assert.Equal(_clock.Now.AddMinutes(10), booking.HoldExpiresAt);

        var created = EventAssert.RaisedOnce<BookingCreated>(booking, 0);
        Assert.Equal(12_000, created.DepositCents);
        Assert.Equal(_clock.Now.AddMinutes(10), created.HoldExpiresAt);
    }

    [Theory]
    [InlineData(5, null)]
    [InlineData(6, 12_000L)]
    [InlineData(8, 16_000L)]
    public void Create_DepositIsTwentyDollarsPerGuestFromSix_BR06(int partySize, long? expectedCents)
    {
        var booking = BookingFactory.Create(partySize: partySize);

        Assert.Equal(expectedCents, booking.Deposit?.Amount.Cents);
        Assert.Equal(expectedCents is null ? BookingStatus.Booked : BookingStatus.PendingPayment, booking.Status);
    }

    [Theory]
    [InlineData(1, 75)]
    [InlineData(2, 75)]
    [InlineData(3, 90)]
    [InlineData(4, 90)]
    [InlineData(5, 120)]
    [InlineData(8, 120)]
    public void Create_TurnTimeFollowsPartySize_BR02(int partySize, int minutes)
    {
        var booking = BookingFactory.Create(partySize: partySize);

        Assert.Equal(minutes, booking.Terms.TurnMinutes);
        Assert.Equal(TimeSpan.FromMinutes(minutes), booking.Period.Duration);
    }

    [Fact]
    public void Create_WhenOnlinePartyOfEight_Succeeds_BR04()
    {
        var booking = BookingFactory.Create(partySize: 8);

        Assert.Equal(8, booking.PartySize.Value);
    }

    [Fact]
    public void Create_WhenOnlinePartyAboveEight_ReturnsPartyTooLarge_BR04()
    {
        var result = Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), BookingChannel.Online, PartySize.Of(9),
            TestData.Start, [TestData.Table8], null, null, _clock.Now);

        EventAssert.Failed(result, ErrorCodes.PartyTooLarge, ErrorKind.Rule);
    }

    [Fact]
    public void Create_WhenPhonePartyAboveEight_Succeeds_BR04()
    {
        var booking = BookingFactory.Create(partySize: 10, channel: BookingChannel.Phone);

        Assert.Equal(BookingChannel.Phone, booking.Channel);
        Assert.Equal(120, booking.Terms.TurnMinutes);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public void Create_PhoneBookingOfSixOrMore_IsBookedWithoutDeposit_BR06(int partySize)
    {
        var booking = BookingFactory.Create(partySize: partySize, channel: BookingChannel.Phone);

        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Null(booking.Deposit);
        Assert.Null(booking.HoldExpiresAt);
        var created = EventAssert.RaisedOnce<BookingCreated>(booking, 0);
        Assert.Null(created.DepositCents);
        Assert.Null(created.HoldExpiresAt);
    }

    [Fact]
    public void CreateWalkIn_OfSixOrMore_HasNoDeposit_BR06()
    {
        var booking = Booking.CreateWalkIn(
            TestData.Venue(), null, TestData.Reference(), PartySize.Of(8), [TestData.Table8], null, TestData.Start).Value;

        Assert.Equal(BookingStatus.Seated, booking.Status);
        Assert.Null(booking.Deposit);
    }

    [Theory]
    [InlineData(17, 0)]
    [InlineData(18, 45)]
    [InlineData(20, 30)]
    public void Create_OnSlotWithinServiceHours_Succeeds_BR01(int hour, int minute)
    {
        var result = CreateAtLocal(hour, minute);

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(16, 45)]
    [InlineData(19, 10)]
    [InlineData(20, 45)]
    public void Create_OffSlotOrOutsideServiceHours_ReturnsOutsideServiceHours_BR01(int hour, int minute)
    {
        var result = CreateAtLocal(hour, minute);

        EventAssert.Failed(result, ErrorCodes.OutsideServiceHours, ErrorKind.Rule);
    }

    [Fact]
    public void Create_WhenStartIsInThePast_ReturnsOutsideServiceHours()
    {
        _clock.Set(TestData.Start.AddMinutes(1));

        var result = Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), BookingChannel.Online, PartySize.Of(2),
            TestData.Start, [TestData.Table4], null, null, _clock.Now);

        EventAssert.Failed(result, ErrorCodes.OutsideServiceHours, ErrorKind.Rule);
    }

    [Fact]
    public void Create_WhenTableIsNotInVenue_ReturnsNotFound()
    {
        var result = Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), BookingChannel.Online, PartySize.Of(2),
            TestData.Start, [new TableId(Guid.NewGuid())], null, null, _clock.Now);

        EventAssert.Failed(result, ErrorCodes.NotFound, ErrorKind.NotFound);
    }

    [Fact]
    public void Create_WithWalkInChannel_Throws()
    {
        Assert.Throws<ArgumentException>(() => Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), BookingChannel.WalkIn, PartySize.Of(2),
            TestData.Start, [TestData.Table4], null, null, _clock.Now));
    }

    [Fact]
    public void Create_CopiesTermsFromVenueSettings()
    {
        var venue = TestData.Venue();

        var booking = BookingFactory.Create(partySize: 6, venue: venue, now: _clock.Now);

        Assert.Equal(venue.Settings.Deposit, booking.Terms.Deposit);
        Assert.Equal(venue.Settings.Cancellation, booking.Terms.Cancellation);
        Assert.Equal(120, booking.Terms.TurnMinutes);
        Assert.Equal(_clock.Now, booking.Terms.CapturedAt);
        Assert.Equal(venue.SettingsVersion, booking.Terms.SettingsVersion);
    }

    [Fact]
    public void Terms_AfterVenueSettingsChange_StayAsBooked_AC56()
    {
        var venue = TestData.Venue();
        var booking = BookingFactory.Create(partySize: 6, venue: venue);
        booking.CaptureDeposit(BookingFactory.PaymentIntent, booking.Deposit!.Amount, true, TestData.Created.AddMinutes(1));
        var termsBefore = booking.Terms;

        venue.UpdateSettings(venue.Settings with
        {
            Deposit = new DepositRule(4, 5000),
            Cancellation = new CancellationRule(48, RefundAmount.None, RefundAmount.None),
        });

        Assert.Same(termsBefore, booking.Terms);
        Assert.Equal(2, venue.SettingsVersion);
        // Cancelling 3 h before still gets the full refund under the terms the guest booked with.
        var decision = booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-3));
        Assert.Equal(RefundDecision.Full, decision.Value);
    }

    // ---------- CreateWalkIn (→ Seated) ----------

    [Fact]
    public void CreateWalkIn_IsSeatedNowForTheTurnTime_RaisesCreatedThenSeated()
    {
        _clock.Set(TestData.Start);

        var booking = Booking.CreateWalkIn(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), PartySize.Of(3), [TestData.Table4], null, _clock.Now).Value;

        Assert.Equal(BookingStatus.Seated, booking.Status);
        Assert.Equal(BookingChannel.WalkIn, booking.Channel);
        Assert.Equal(_clock.Now, booking.SeatedAt);
        Assert.Equal(new TimeRange(_clock.Now, _clock.Now.AddMinutes(90)), booking.Period);
        Assert.Null(booking.Deposit);

        var events = EventAssert.Raised(booking, 0, typeof(BookingCreated), typeof(BookingSeated));
        var seated = (BookingSeated)events[1];
        Assert.Equal(SeatSource.WalkIn, seated.Source);
        Assert.Equal([TestData.Table4], seated.TableIds);
        Assert.Equal(3, seated.PartySize);
        Assert.Null(seated.WaitlistEntryId);
        Assert.Equal(_clock.Now, seated.SeatedAt);
    }

    [Fact]
    public void CreateWalkIn_FromWaitlist_CarriesWaitlistEntryId()
    {
        var entryId = WaitlistEntryId.New(_clock.Now);

        var booking = Booking.CreateWalkIn(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), PartySize.Of(2), [TestData.Table2], entryId, _clock.Now).Value;

        var seated = Assert.IsType<BookingSeated>(booking.DomainEvents[1].Event);
        Assert.Equal(entryId, seated.WaitlistEntryId);
    }

    [Fact]
    public void CreateWalkIn_WhenTableIsNotInVenue_ReturnsNotFound()
    {
        var result = Booking.CreateWalkIn(
            TestData.Venue(), null, TestData.Reference(), PartySize.Of(2), [new TableId(Guid.NewGuid())], null, _clock.Now);

        EventAssert.Failed(result, ErrorCodes.NotFound, ErrorKind.NotFound);
    }

    // ---------- CaptureDeposit (PendingPayment → Booked / Cancelled; D7) ----------

    [Fact]
    public void CaptureDeposit_WhenPendingAndHoldActive_BooksAndRaisesDepositCaptured()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: false, TestData.Created.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        Assert.Equal("pi_1", booking.Deposit.PaymentIntentId);
        Assert.Null(booking.HoldExpiresAt);
        var captured = EventAssert.RaisedOnce<DepositCaptured>(booking, before);
        Assert.Equal("pi_1", captured.PaymentIntentId);
        Assert.Equal(12_000, captured.AmountCents);
    }

    [Fact]
    public void CaptureDeposit_WhenHoldExpiredButTableStillFree_Books_D7()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: true, TestData.Created.AddMinutes(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Booked, booking.Status);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        EventAssert.RaisedOnce<DepositCaptured>(booking, before);
    }

    [Fact]
    public void CaptureDeposit_WhenHoldExpiredAndTableTaken_CancelsWithFullRefund_D7()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: false, TestData.Created.AddMinutes(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelReason.HoldExpired, booking.CancellationReason);
        Assert.Equal(RefundDecision.Full, booking.CancellationRefund);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        Assert.Equal("pi_1", booking.Deposit.PaymentIntentId);
        Assert.All(booking.Tables, t => Assert.False(t.Active));
        var cancelled = EventAssert.RaisedOnce<BookingCancelled>(booking, before);
        Assert.Equal(CancelReason.HoldExpired, cancelled.Reason);
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);
        Assert.Equal(ActorType.System, cancelled.CancelledBy.Type);
    }

    [Fact]
    public void CaptureDeposit_WhenAlreadyCancelledByHoldExpiry_ReturnsHoldExpiredAndChangesNothing_D7()
    {
        var booking = BookingFactory.PendingPayment();
        booking.ExpireHold(TestData.Created.AddMinutes(10));
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), tableStillFree: true, TestData.Created.AddMinutes(12));

        EventAssert.Failed(result, ErrorCodes.HoldExpired, ErrorKind.Conflict);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(DepositStatus.Released, booking.Deposit!.Status);
        Assert.Null(booking.Deposit.PaymentIntentId);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void CaptureDeposit_WhenAmountDiffersFromDepositDue_ReturnsDepositAmountMismatch()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(10_000), true, TestData.Created.AddMinutes(1));

        EventAssert.Failed(result, ErrorCodes.DepositAmountMismatch, ErrorKind.Rule);
        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void CaptureDeposit_WhenAlreadyCapturedWithSamePayment_IsNoOp()
    {
        var booking = BookingFactory.BookedWithDeposit();
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit(BookingFactory.PaymentIntent, Money.FromNzd(12_000), true, TestData.Created.AddMinutes(3));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Booked, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public void CaptureDeposit_WhenNotPendingPayment_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), true, TestData.Created.AddHours(2));

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- ExpireHold (PendingPayment → Cancelled) ----------

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    public void ExpireHold_WhenHoldHasEnded_CancelsAndReleasesDeposit(int minutesAfterCreate)
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.ExpireHold(TestData.Created.AddMinutes(minutesAfterCreate));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelReason.HoldExpired, booking.CancellationReason);
        Assert.Equal(DepositStatus.Released, booking.Deposit!.Status);
        Assert.All(booking.Tables, t => Assert.False(t.Active));
        var cancelled = EventAssert.RaisedOnce<BookingCancelled>(booking, before);
        Assert.Equal(CancelReason.HoldExpired, cancelled.Reason);
        Assert.Equal(RefundDecision.NotApplicable, cancelled.RefundDecision);
    }

    [Fact]
    public void ExpireHold_BeforeHoldEnds_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.PendingPayment();
        var before = booking.LastEventSequence;

        var result = booking.ExpireHold(TestData.Created.AddMinutes(9));

        EventAssert.InvalidTransition(result);
        Assert.Equal(BookingStatus.PendingPayment, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void ExpireHold_WhenAlreadyCancelled_IsNoOp()
    {
        var booking = BookingFactory.PendingPayment();
        booking.ExpireHold(TestData.Created.AddMinutes(10));
        var before = booking.LastEventSequence;

        var result = booking.ExpireHold(TestData.Created.AddMinutes(11));

        Assert.True(result.IsSuccess);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Completed)]
    public void ExpireHold_WhenNotPendingPayment_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.ExpireHold(TestData.Start.AddHours(3));

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- Confirm (Booked → Confirmed) ----------

    [Fact]
    public void Confirm_WhenBookedBeforeStart_ConfirmsAndRaisesBookingConfirmed()
    {
        var booking = BookingFactory.Create();
        var before = booking.LastEventSequence;

        var result = booking.Confirm(TestData.GuestActor, TestData.Start.AddMinutes(-1));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        var confirmed = EventAssert.RaisedOnce<BookingConfirmed>(booking, before);
        Assert.Equal(TestData.GuestActor, confirmed.ConfirmedBy);
    }

    [Fact]
    public void Confirm_AfterReminder_RecordsTheResponse()
    {
        var booking = BookingFactory.Create();
        booking.RecordReminder(TestData.Start.AddHours(-24), "Free cancellation until 2 hours before.");
        var confirmAt = TestData.Start.AddHours(-20);

        booking.Confirm(TestData.GuestActor, confirmAt);

        Assert.Equal(ReminderResponse.Confirmed, booking.Reminder!.Response);
        Assert.Equal(confirmAt, booking.Reminder.RespondedAt);
    }

    [Fact]
    public void Confirm_WhenAlreadyConfirmed_IsNoOp()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Confirmed);
        var before = booking.LastEventSequence;

        var result = booking.Confirm(TestData.GuestActor, TestData.Created.AddHours(2));

        Assert.True(result.IsSuccess);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void Confirm_AtOrAfterStart_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.Create();
        var before = booking.LastEventSequence;

        var result = booking.Confirm(TestData.GuestActor, TestData.Start);

        EventAssert.InvalidTransition(result);
        Assert.Equal(BookingStatus.Booked, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public void Confirm_WhenNotBooked_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.Confirm(TestData.GuestActor, TestData.Created.AddHours(2));

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- Cancel (→ Cancelled) ----------

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    public void Cancel_FromUpcomingStatus_CancelsReleasesTablesAndRaisesBookingCancelled(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Created.AddHours(3));

        Assert.True(result.IsSuccess);
        Assert.Equal(RefundDecision.NotApplicable, result.Value);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(CancelReason.GuestRequest, booking.CancellationReason);
        Assert.False(booking.IsActive);
        Assert.All(booking.Tables, t => Assert.False(t.Active));
        var cancelled = EventAssert.RaisedOnce<BookingCancelled>(booking, before);
        Assert.Equal(CancelReason.GuestRequest, cancelled.Reason);
        Assert.Equal(TestData.GuestActor, cancelled.CancelledBy);
        Assert.Equal(RefundDecision.NotApplicable, cancelled.RefundDecision);
    }

    [Fact]
    public void Cancel_WhenPendingPayment_ReleasesDeposit()
    {
        var booking = BookingFactory.PendingPayment();

        booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Created.AddMinutes(3));

        Assert.Equal(DepositStatus.Released, booking.Deposit!.Status);
    }

    [Fact]
    public void Cancel_ByGuestExactlyTwoHoursBefore_RefundsInFull_BR07()
    {
        var booking = BookingFactory.BookedWithDeposit();
        var before = booking.LastEventSequence;

        var result = booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-2));

        Assert.Equal(RefundDecision.Full, result.Value);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        var cancelled = EventAssert.RaisedOnce<BookingCancelled>(booking, before);
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);
    }

    [Fact]
    public void Cancel_ByGuestLessThanTwoHoursBefore_KeepsDeposit_BR07()
    {
        var booking = BookingFactory.BookedWithDeposit();
        var before = booking.LastEventSequence;

        var result = booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-2).AddMinutes(1));

        Assert.Equal(RefundDecision.None, result.Value);
        Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
        var cancelled = EventAssert.RaisedOnce<BookingCancelled>(booking, before);
        Assert.Equal(RefundDecision.None, cancelled.RefundDecision);
    }

    [Fact]
    public void Cancel_ByHostLate_StillRefundsInFull_BR07()
    {
        var booking = BookingFactory.BookedWithDeposit();

        var result = booking.Cancel(TestData.Host, CancelReason.HostRequest, TestData.Start.AddMinutes(-10));

        Assert.Equal(RefundDecision.Full, result.Value);
        Assert.Equal(RefundDecision.Full, booking.CancellationRefund);
    }

    [Fact]
    public void Cancel_ByGuestAfterReminder_RecordsTheResponse()
    {
        var booking = BookingFactory.Create();
        booking.RecordReminder(TestData.Start.AddHours(-24), "Free cancellation until 2 hours before.");

        booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-20));

        Assert.Equal(ReminderResponse.Cancelled, booking.Reminder!.Response);
    }

    [Fact]
    public void Cancel_WhenAlreadyCancelled_IsNoOpAndReturnsTheStoredDecision()
    {
        var booking = BookingFactory.BookedWithDeposit();
        booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-3));
        var before = booking.LastEventSequence;

        // A retry after the 2-hour line gets the same answer as at cancellation (§8.3).
        var result = booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddMinutes(-30));

        Assert.True(result.IsSuccess);
        Assert.Equal(RefundDecision.Full, result.Value);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Completed)]
    public void Cancel_WhenSeatedOrCompleted_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.Cancel(TestData.Host, CancelReason.HostRequest, TestData.Start.AddHours(2));

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- Seat (Booked / Confirmed → Seated) ----------

    [Theory]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    public void Seat_WhenUpcoming_SeatsForTurnTimeFromNow(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;
        _clock.Set(TestData.Start.AddMinutes(-5));

        var result = booking.Seat([TestData.Table4], _clock.Now, TestData.Host);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Seated, booking.Status);
        Assert.Equal(_clock.Now, booking.SeatedAt);
        Assert.Equal(new TimeRange(_clock.Now, _clock.Now.AddMinutes(75)), booking.Period);
        var seated = EventAssert.RaisedOnce<BookingSeated>(booking, before);
        Assert.Equal(SeatSource.Booking, seated.Source);
        Assert.Equal([TestData.Table4], seated.TableIds);
        Assert.Equal(2, seated.PartySize);
        Assert.Null(seated.WaitlistEntryId);
    }

    [Fact]
    public void Seat_AtOtherTables_ReassignsTables()
    {
        var booking = BookingFactory.Create();

        booking.Seat([TestData.Table2, TestData.Table6], TestData.Start, TestData.Host);

        Assert.Equal([TestData.Table2, TestData.Table6], booking.Tables.Select(t => t.TableId));
        Assert.All(booking.Tables, t =>
        {
            Assert.True(t.Active);
            Assert.Equal(booking.Period, t.Period);
        });
    }

    [Fact]
    public void Seat_WhenAlreadySeated_IsNoOp()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Seated);
        var before = booking.LastEventSequence;
        var period = booking.Period;

        var result = booking.Seat([TestData.Table4], TestData.Start.AddMinutes(5), TestData.Host);

        Assert.True(result.IsSuccess);
        Assert.Equal(period, booking.Period);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public void Seat_WhenNotBookedOrConfirmed_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.Seat([TestData.Table4], TestData.Start, TestData.Host);

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void Seat_WhenCancelled_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Cancelled);

        EventAssert.InvalidTransition(booking.Seat([TestData.Table4], TestData.Start, TestData.Host));
    }

    // ---------- Complete (Seated → Completed) ----------

    [Fact]
    public void Complete_WhenBillClosed_StoresBillAndRaisesCompletedThenCleared()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Seated);
        var before = booking.LastEventSequence;
        var closedAt = TestData.Start.AddMinutes(65);
        var bill = TestData.Bill(closedAt);

        var result = booking.Complete(CompletionSource.BillClosed, bill, closedAt.AddSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Completed, booking.Status);
        Assert.Equal(CompletionSource.BillClosed, booking.CompletedBy);
        Assert.Equal(closedAt.AddSeconds(5), booking.CompletedAt);
        Assert.Equal([bill], booking.Bills);
        Assert.Equal(Money.FromNzd(18_500), booking.BillTotal);
        Assert.All(booking.Tables, t => Assert.False(t.Active));

        var events = EventAssert.Raised(booking, before, typeof(BookingCompleted), typeof(TableCleared));
        var completed = (BookingCompleted)events[0];
        Assert.Equal(CompletionSource.BillClosed, completed.Source);
        Assert.Equal("B-1001", completed.PosBillId);
        Assert.Equal(18_500, completed.BillTotalCents);
        Assert.Equal(closedAt, completed.ClosedAt);
        var cleared = (TableCleared)events[1];
        Assert.Equal([TestData.Table4], cleared.TableIds);
    }

    [Fact]
    public void Complete_WhenClearedByHost_HasNoBill()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Seated);
        var before = booking.LastEventSequence;

        var result = booking.Complete(CompletionSource.ClearedByHost, null, BookingFactory.ClearedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(CompletionSource.ClearedByHost, booking.CompletedBy);
        Assert.Empty(booking.Bills);
        Assert.Null(booking.BillTotal);
        var events = EventAssert.Raised(booking, before, typeof(BookingCompleted), typeof(TableCleared));
        var completed = (BookingCompleted)events[0];
        Assert.Null(completed.PosBillId);
        Assert.Null(completed.BillTotalCents);
        Assert.Equal(BookingFactory.ClearedAt, completed.ClosedAt);
    }

    [Fact]
    public void Complete_WhenAlreadyCompleted_IsNoOp()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Completed);
        var before = booking.LastEventSequence;

        var result = booking.Complete(CompletionSource.ClearedByHost, null, BookingFactory.ClearedAt.AddMinutes(1));

        Assert.True(result.IsSuccess);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Cancelled)]
    public void Complete_WhenNotSeated_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.Complete(CompletionSource.ClearedByHost, null, TestData.Start);

        EventAssert.InvalidTransition(result);
        Assert.Equal(status, booking.Status);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void Complete_WithBillClosedButNoBill_Throws()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Seated);

        Assert.Throws<ArgumentException>(() => booking.Complete(CompletionSource.BillClosed, null, TestData.Start));
    }

    // ---------- AttachLateBill (Completed → Completed) ----------

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(30)]
    [InlineData(-30)]
    public void AttachLateBill_WhenClearedByHostAndInsideWindow_AttachesAndRaisesBookingBillAttached(int minutesFromClear)
    {
        var booking = BookingFactory.ClearedByHost();
        var before = booking.LastEventSequence;
        var bill = TestData.Bill(BookingFactory.ClearedAt.AddMinutes(minutesFromClear));
        var now = BookingFactory.ClearedAt.AddMinutes(40);

        var result = booking.AttachLateBill(bill, BillMatchedBy.LateMatch, 30, now);

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingStatus.Completed, booking.Status);
        Assert.Equal([bill], booking.Bills);
        Assert.Equal(bill.Total, booking.BillTotal);
        var attached = EventAssert.RaisedOnce<BookingBillAttached>(booking, before);
        Assert.Equal("B-1001", attached.PosBillId);
        Assert.Equal(18_500, attached.BillTotalCents);
        Assert.Equal(bill.SourceMessage, attached.PosMessageId);
        Assert.Equal(BillMatchedBy.LateMatch, attached.MatchedBy);
        Assert.Equal(now, attached.AttachedAt);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(-31)]
    public void AttachLateBill_WhenOutsideWindow_ReturnsOutsideLateMatchWindow(int minutesFromClear)
    {
        var booking = BookingFactory.ClearedByHost();
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(
            TestData.Bill(BookingFactory.ClearedAt.AddMinutes(minutesFromClear)), BillMatchedBy.LateMatch, 30, BookingFactory.ClearedAt.AddHours(1));

        EventAssert.Failed(result, ErrorCodes.OutsideLateMatchWindow, ErrorKind.Rule);
        Assert.Empty(booking.Bills);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void AttachLateBill_WhenMatchedByManagerOutsideWindow_Attaches()
    {
        var booking = BookingFactory.ClearedByHost();
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(
            TestData.Bill(BookingFactory.ClearedAt.AddHours(3)), BillMatchedBy.Manual, 30, BookingFactory.ClearedAt.AddHours(4));

        Assert.True(result.IsSuccess);
        Assert.Single(booking.Bills);
        var attached = EventAssert.RaisedOnce<BookingBillAttached>(booking, before);
        Assert.Equal(BillMatchedBy.Manual, attached.MatchedBy);
    }

    [Fact]
    public void AttachLateBill_WhenSameBillAgain_IsNoOp()
    {
        var booking = BookingFactory.ClearedByHost();
        var bill = TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5));
        booking.AttachLateBill(bill, BillMatchedBy.LateMatch, 30, BookingFactory.ClearedAt.AddMinutes(6));
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(bill, BillMatchedBy.LateMatch, 30, BookingFactory.ClearedAt.AddMinutes(7));

        Assert.True(result.IsSuccess);
        Assert.Single(booking.Bills);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BillMatchedBy.LateMatch)]
    [InlineData(BillMatchedBy.Manual)]
    public void AttachLateBill_WhenADifferentBillIsAlreadyAttached_ReturnsSecondBill(BillMatchedBy matchedBy)
    {
        var booking = BookingFactory.ClearedByHost();
        booking.AttachLateBill(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5)), BillMatchedBy.LateMatch, 30, BookingFactory.ClearedAt.AddMinutes(6));
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(
            TestData.Bill(BookingFactory.ClearedAt.AddMinutes(8), posBillId: "B-1002"), matchedBy, 30, BookingFactory.ClearedAt.AddMinutes(9));

        EventAssert.Failed(result, ErrorCodes.SecondBill, ErrorKind.Rule);
        Assert.Single(booking.Bills);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void AttachLateBill_WhenCompletedByBillClosed_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Seated);
        booking.Complete(CompletionSource.BillClosed, TestData.Bill(TestData.Start.AddMinutes(60)), TestData.Start.AddMinutes(60));
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(
            TestData.Bill(TestData.Start.AddMinutes(62), posBillId: "B-1002"), BillMatchedBy.Manual, 30, TestData.Start.AddMinutes(63));

        EventAssert.InvalidTransition(result);
        EventAssert.NoneRaised(booking, before);
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Booked)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Seated)]
    [InlineData(BookingStatus.Cancelled)]
    public void AttachLateBill_WhenNotCompleted_ReturnsInvalidTransition(BookingStatus status)
    {
        var booking = BookingFactory.InStatus(status);
        var before = booking.LastEventSequence;

        var result = booking.AttachLateBill(TestData.Bill(TestData.Start), BillMatchedBy.Manual, 30, TestData.Start);

        EventAssert.InvalidTransition(result);
        Assert.Empty(booking.Bills);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- RecordRefund (deposit Captured → Refunded) ----------

    [Fact]
    public void RecordRefund_WhenDepositCaptured_RefundsAndRaisesDepositRefunded()
    {
        var booking = BookingFactory.BookedWithDeposit();
        booking.Cancel(TestData.GuestActor, CancelReason.GuestRequest, TestData.Start.AddHours(-5));
        var before = booking.LastEventSequence;

        var result = booking.RecordRefund("re_1", Money.FromNzd(12_000), TestData.Start.AddHours(-4));

        Assert.True(result.IsSuccess);
        Assert.Equal(DepositStatus.Refunded, booking.Deposit!.Status);
        Assert.Equal("re_1", booking.Deposit.RefundId);
        var refunded = EventAssert.RaisedOnce<DepositRefunded>(booking, before);
        Assert.Equal("re_1", refunded.RefundId);
        Assert.Equal(12_000, refunded.AmountCents);
    }

    [Fact]
    public void RecordRefund_WhenAlreadyRefunded_IsNoOp()
    {
        var booking = BookingFactory.BookedWithDeposit();
        booking.Cancel(TestData.Host, CancelReason.HostRequest, TestData.Start.AddHours(-5));
        booking.RecordRefund("re_1", Money.FromNzd(12_000), TestData.Start.AddHours(-4));
        var before = booking.LastEventSequence;

        var result = booking.RecordRefund("re_1", Money.FromNzd(12_000), TestData.Start.AddHours(-3));

        Assert.True(result.IsSuccess);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void RecordRefund_WhenNoDeposit_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Cancelled);

        EventAssert.InvalidTransition(booking.RecordRefund("re_1", Money.FromNzd(100), TestData.Start));
    }

    [Fact]
    public void RecordRefund_WhenDepositNotCaptured_ReturnsInvalidTransition()
    {
        var booking = BookingFactory.PendingPayment();
        booking.ExpireHold(TestData.Created.AddMinutes(10));
        var before = booking.LastEventSequence;

        var result = booking.RecordRefund("re_1", Money.FromNzd(12_000), TestData.Created.AddMinutes(20));

        EventAssert.InvalidTransition(result);
        Assert.Equal(DepositStatus.Released, booking.Deposit!.Status);
        EventAssert.NoneRaised(booking, before);
    }

    // ---------- RecordReminder and the late-cancellation warning (BR-13) ----------

    [Fact]
    public void RecordReminder_WhenMadeMoreThan24HoursAhead_RecordsWithoutEvent_BR13()
    {
        var booking = BookingFactory.Create();
        var before = booking.LastEventSequence;
        var sentAt = TestData.Start.AddHours(-24);

        var result = booking.RecordReminder(sentAt, "Free cancellation until 2 hours before.");

        Assert.True(result.IsSuccess);
        Assert.Equal(new ReminderRecord(sentAt, "Free cancellation until 2 hours before."), booking.Reminder);
        EventAssert.NoneRaised(booking, before);
    }

    [Fact]
    public void RecordReminder_WhenBookingWasMadeLessThan24HoursAhead_ReturnsReminderNotAllowed_BR13()
    {
        var booking = BookingFactory.Create(now: TestData.Start.AddHours(-23));

        var result = booking.RecordReminder(TestData.Start.AddHours(-22), "policy");

        EventAssert.Failed(result, ErrorCodes.ReminderNotAllowed, ErrorKind.Rule);
        Assert.Null(booking.Reminder);
    }

    [Theory]
    [InlineData(119, false)]
    [InlineData(120, true)]
    public void RecordReminder_WhenBookingStartsWithinTwoHours_ReturnsReminderNotAllowed_BR13(int minutesBeforeStart, bool allowed)
    {
        var booking = BookingFactory.Create();

        var result = booking.RecordReminder(TestData.Start.AddMinutes(-minutesBeforeStart), "policy");

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            EventAssert.Failed(result, ErrorCodes.ReminderNotAllowed, ErrorKind.Rule);
        }
    }

    [Fact]
    public void RecordReminder_WhenAlreadyConfirmed_ReturnsReminderNotAllowed_BR13()
    {
        var booking = BookingFactory.InStatus(BookingStatus.Confirmed);

        EventAssert.Failed(booking.RecordReminder(TestData.Start.AddHours(-24), "policy"), ErrorCodes.ReminderNotAllowed, ErrorKind.Rule);
    }

    [Fact]
    public void RecordReminder_WhenAlreadySent_IsNoOp()
    {
        var booking = BookingFactory.Create();
        var first = TestData.Start.AddHours(-24);
        booking.RecordReminder(first, "policy");

        var result = booking.RecordReminder(TestData.Start.AddHours(-23), "other");

        Assert.True(result.IsSuccess);
        Assert.Equal(first, booking.Reminder!.SentAt);
    }

    [Theory]
    [InlineData(121, false)]
    [InlineData(120, false)]
    [InlineData(119, true)]
    public void CancellingNowKeepsDeposit_InsideFreeCancellationWindow_IsTrue_BR13(int minutesBeforeStart, bool keeps)
    {
        var booking = BookingFactory.BookedWithDeposit();

        Assert.Equal(keeps, booking.CancellingNowKeepsDeposit(TestData.Start.AddMinutes(-minutesBeforeStart)));
    }

    [Fact]
    public void CancellingNowKeepsDeposit_WithoutDeposit_IsFalse_BR13()
    {
        var booking = BookingFactory.Create();

        Assert.False(booking.CancellingNowKeepsDeposit(TestData.Start.AddMinutes(-30)));
    }

    // ---------- Event sequence across a whole life ----------

    [Fact]
    public void FullLifecycle_RaisesEachEventOnceWithConsecutiveSequenceNumbers()
    {
        var booking = BookingFactory.PendingPayment();
        booking.CaptureDeposit("pi_1", Money.FromNzd(12_000), false, TestData.Created.AddMinutes(1));
        booking.RecordReminder(TestData.Start.AddHours(-24), "policy");
        booking.Confirm(TestData.GuestActor, TestData.Start.AddHours(-23));
        booking.Seat([TestData.Table8], TestData.Start, TestData.Host);
        booking.Complete(CompletionSource.ClearedByHost, null, TestData.Start.AddMinutes(100));
        booking.AttachLateBill(TestData.Bill(TestData.Start.AddMinutes(105)), BillMatchedBy.LateMatch, 30, TestData.Start.AddMinutes(106));

        Assert.Equal(
            [typeof(BookingCreated), typeof(DepositCaptured), typeof(BookingConfirmed), typeof(BookingSeated),
             typeof(BookingCompleted), typeof(TableCleared), typeof(BookingBillAttached)],
            booking.DomainEvents.Select(e => e.Event.GetType()));
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], booking.DomainEvents.Select(e => e.Sequence));
        Assert.Equal(7, booking.LastEventSequence);
        Assert.Equal(7, booking.DomainEvents.Select(e => e.Event.EventId).Distinct().Count());
    }

    private static Result<Booking> CreateAtLocal(int hour, int minute)
    {
        // 20 November 2026 is NZDT (UTC+13).
        var start = new DateTimeOffset(2026, 11, 20, hour, minute, 0, TimeSpan.FromHours(13));
        return Booking.Create(
            TestData.Venue(), TestData.GuestId, TestData.Reference(), BookingChannel.Online, PartySize.Of(2),
            start, [TestData.Table4], null, null, TestData.Created);
    }
}
