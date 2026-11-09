using Reservation.Domain.Bookings;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>Late-match rule (Design §6.5, steps 1–4; §6.6 duplicates and second bills).</summary>
public class LateMatchRuleTests
{
    private const int Window = 30;

    [Fact]
    public void Match_Step1_WhenBookingByIdIsSeated_Completes()
    {
        var seated = BookingFactory.InStatus(BookingStatus.Seated);

        var match = LateMatchRule.Match(TestData.Bill(TestData.Start.AddMinutes(60)), seated, null, [], Window);

        Assert.Same(seated, Assert.IsType<BillMatch.CompleteSeated>(match).Booking);
    }

    [Fact]
    public void Match_Step1_WhenBookingByIdWasClearedWithoutBill_AttachesLate()
    {
        var cleared = BookingFactory.ClearedByHost();

        var match = LateMatchRule.Match(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5)), cleared, null, [], Window);

        Assert.Same(cleared, Assert.IsType<BillMatch.AttachLate>(match).Booking);
    }

    [Fact]
    public void Match_Step1_WhenBookingByIdHasADifferentBill_IsUnmatchedSecondBill()
    {
        var cleared = BookingFactory.ClearedByHost();
        cleared.AttachLateBill(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(1)), BillMatchedBy.LateMatch, Window, BookingFactory.ClearedAt.AddMinutes(2));

        var match = LateMatchRule.Match(
            TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5), posBillId: "B-2"), cleared, null, [], Window);

        Assert.Equal(UnmatchedReasons.SecondBill, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_Step1_WhenBookingByIdIsNotSeatedOrCleared_IsUnmatched()
    {
        var booked = BookingFactory.Create();

        var match = LateMatchRule.Match(TestData.Bill(TestData.Start), booked, null, [], Window);

        Assert.Equal(UnmatchedReasons.NoMatch, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_Step2_WhenABookingIsSeatedAtTheTable_Completes()
    {
        var seated = BookingFactory.InStatus(BookingStatus.Seated);
        var cleared = BookingFactory.ClearedByHost();

        var match = LateMatchRule.Match(TestData.Bill(TestData.Start.AddMinutes(60)), null, seated, [cleared], Window);

        Assert.Same(seated, Assert.IsType<BillMatch.CompleteSeated>(match).Booking);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-10)]
    [InlineData(30)]
    public void Match_Step3_WhenExactlyOneClearedBookingInsideWindow_AttachesLate(int minutesFromClear)
    {
        var cleared = BookingFactory.ClearedByHost();

        var match = LateMatchRule.Match(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(minutesFromClear)), null, null, [cleared], Window);

        Assert.Same(cleared, Assert.IsType<BillMatch.AttachLate>(match).Booking);
    }

    [Fact]
    public void Match_Step3_WhenTwoCandidates_IsUnmatchedAmbiguous()
    {
        var first = BookingFactory.ClearedByHost();
        var second = BookingFactory.ClearedByHost();

        var match = LateMatchRule.Match(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5)), null, null, [first, second], Window);

        Assert.Equal(UnmatchedReasons.Ambiguous, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_Step4_WhenClearedOutsideWindow_IsUnmatched()
    {
        var cleared = BookingFactory.ClearedByHost();

        var match = LateMatchRule.Match(TestData.Bill(BookingFactory.ClearedAt.AddMinutes(31)), null, null, [cleared], Window);

        Assert.Equal(UnmatchedReasons.NoMatch, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_Step4_WhenOnlyBookingCompletedByBillClosed_IsUnmatched()
    {
        var completed = BookingFactory.InStatus(BookingStatus.Seated);
        completed.Complete(CompletionSource.BillClosed, TestData.Bill(TestData.Start.AddMinutes(60)), TestData.Start.AddMinutes(60));

        var match = LateMatchRule.Match(
            TestData.Bill(TestData.Start.AddMinutes(65), posBillId: "B-2"), null, null, [completed], Window);

        Assert.Equal(UnmatchedReasons.NoMatch, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_Step4_WhenNoCandidates_IsUnmatched()
    {
        var match = LateMatchRule.Match(TestData.Bill(TestData.Start), null, null, [], Window);

        Assert.Equal(UnmatchedReasons.NoMatch, Assert.IsType<BillMatch.Unmatched>(match).Reason);
    }

    [Fact]
    public void Match_WhenBillAlreadyRecorded_IsDuplicate()
    {
        var cleared = BookingFactory.ClearedByHost();
        var bill = TestData.Bill(BookingFactory.ClearedAt.AddMinutes(5));
        cleared.AttachLateBill(bill, BillMatchedBy.LateMatch, Window, BookingFactory.ClearedAt.AddMinutes(6));

        var match = LateMatchRule.Match(bill, null, null, [cleared], Window);

        Assert.Same(cleared, Assert.IsType<BillMatch.Duplicate>(match).Booking);
    }
}
