using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Waitlist;

/// <summary>Waitlist entry aggregate: every row of Design §3.2 and the rejected transitions.</summary>
public class WaitlistEntryTests
{
    private static readonly DateTimeOffset _joinedAt = TestData.Start;
    private static readonly VenueId _venueId = new(Guid.CreateVersion7(TestData.Created));

    // ---------- Join ----------

    [Fact]
    public void Join_WithValidInput_IsWaitingAndRaisesWaitlistJoined()
    {
        var entry = WaitlistEntry.Join(_venueId, TestData.GuestId, TestData.ContactName, PartySize.Of(4), 25, _joinedAt).Value;

        Assert.Equal(WaitlistStatus.Waiting, entry.Status);
        Assert.Equal(_joinedAt, entry.JoinedAt);
        Assert.Equal(TestData.ContactName, entry.ContactName);
        Assert.True(entry.IsActive);
        var joined = EventAssert.RaisedOnce<WaitlistJoined>(entry, 0);
        Assert.Equal(entry.Id, joined.WaitlistEntryId);
        Assert.Equal(TestData.GuestId, joined.GuestId);
        Assert.Equal(4, joined.PartySize);
        Assert.Equal(25, joined.QuotedMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(240)]
    public void Join_WithQuotedWaitAtTheLimits_Succeeds(int minutes)
    {
        Assert.True(WaitlistEntry.Join(_venueId, TestData.GuestId, TestData.ContactName, PartySize.Of(2), minutes, _joinedAt).IsSuccess);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(241)]
    public void Join_WithQuotedWaitOutOfRange_ReturnsValidationFailed(int minutes)
    {
        var result = WaitlistEntry.Join(_venueId, TestData.GuestId, TestData.ContactName, PartySize.Of(2), minutes, _joinedAt);

        EventAssert.Failed(result, ErrorCodes.ValidationFailed, ErrorKind.Validation);
        Assert.Contains("quotedWaitMinutes", result.Error!.Fields!.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Join_WithPartySizeOutOfRange_IsRejectedByPartySize(int size)
    {
        EventAssert.Failed(PartySize.Create(size), ErrorCodes.ValidationFailed, ErrorKind.Validation);
    }

    // ---------- Notify ----------

    [Fact]
    public void Notify_WhenWaiting_IsNotifiedAndRaisesWaitlistNotified()
    {
        var entry = Waiting();
        var before = entry.LastEventSequence;
        var at = _joinedAt.AddMinutes(20);

        var result = entry.Notify(TestData.Table4, at);

        Assert.True(result.IsSuccess);
        Assert.Equal(WaitlistStatus.Notified, entry.Status);
        Assert.Equal(at, entry.NotifiedAt);
        Assert.Equal(TestData.Table4, entry.NotifiedTableId);
        var notified = EventAssert.RaisedOnce<WaitlistNotified>(entry, before);
        Assert.Equal(TestData.Table4, notified.TableId);
        Assert.Equal(at, notified.NotifiedAt);
    }

    [Fact]
    public void Notify_WhenAlreadyNotified_RenotifiesAndRaisesAgain()
    {
        var entry = Waiting();
        entry.Notify(TestData.Table4, _joinedAt.AddMinutes(20));
        var before = entry.LastEventSequence;
        var again = _joinedAt.AddMinutes(25);

        var result = entry.Notify(TestData.Table6, again);

        Assert.True(result.IsSuccess);
        Assert.Equal(again, entry.NotifiedAt);
        Assert.Equal(TestData.Table6, entry.NotifiedTableId);
        EventAssert.RaisedOnce<WaitlistNotified>(entry, before);
    }

    [Theory]
    [InlineData(WaitlistStatus.Seated)]
    [InlineData(WaitlistStatus.WalkedAway)]
    [InlineData(WaitlistStatus.Removed)]
    public void Notify_WhenEnded_ReturnsInvalidTransition(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        EventAssert.InvalidTransition(entry.Notify(TestData.Table4, _joinedAt.AddHours(1)));
        Assert.Equal(status, entry.Status);
        EventAssert.NoneRaised(entry, before);
    }

    // ---------- MarkSeated / WalkAway / Remove ----------

    [Theory]
    [InlineData(WaitlistStatus.Waiting)]
    [InlineData(WaitlistStatus.Notified)]
    public void MarkSeated_WhenActive_IsSeatedAndRaisesWaitlistEndedSeated(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;
        var bookingId = BookingId.New(_joinedAt);

        var result = entry.MarkSeated(bookingId, _joinedAt.AddMinutes(30));

        Assert.True(result.IsSuccess);
        Assert.Equal(WaitlistStatus.Seated, entry.Status);
        Assert.Equal(bookingId, entry.BookingId);
        Assert.Equal(_joinedAt.AddMinutes(30), entry.EndedAt);
        var ended = EventAssert.RaisedOnce<WaitlistEnded>(entry, before);
        Assert.Equal(WaitlistOutcome.Seated, ended.Outcome);
        Assert.Equal(bookingId, ended.BookingId);
    }

    [Theory]
    [InlineData(WaitlistStatus.Waiting)]
    [InlineData(WaitlistStatus.Notified)]
    public void WalkAway_WhenActive_RaisesWaitlistEndedWalkedAway(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        Assert.True(entry.WalkAway(_joinedAt.AddMinutes(40)).IsSuccess);

        Assert.Equal(WaitlistStatus.WalkedAway, entry.Status);
        var ended = EventAssert.RaisedOnce<WaitlistEnded>(entry, before);
        Assert.Equal(WaitlistOutcome.WalkedAway, ended.Outcome);
        Assert.Null(ended.BookingId);
    }

    [Theory]
    [InlineData(WaitlistStatus.Waiting)]
    [InlineData(WaitlistStatus.Notified)]
    public void Remove_WhenActive_RaisesWaitlistEndedRemoved(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        Assert.True(entry.Remove(TestData.Host, _joinedAt.AddMinutes(40)).IsSuccess);

        Assert.Equal(WaitlistStatus.Removed, entry.Status);
        var ended = EventAssert.RaisedOnce<WaitlistEnded>(entry, before);
        Assert.Equal(WaitlistOutcome.Removed, ended.Outcome);
    }

    [Theory]
    [InlineData(WaitlistStatus.Seated)]
    [InlineData(WaitlistStatus.WalkedAway)]
    [InlineData(WaitlistStatus.Removed)]
    public void EndingAgainInTheSameWay_IsNoOp(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        var result = status switch
        {
            WaitlistStatus.Seated => entry.MarkSeated(entry.BookingId!.Value, _joinedAt.AddHours(1)),
            WaitlistStatus.WalkedAway => entry.WalkAway(_joinedAt.AddHours(1)),
            _ => entry.Remove(TestData.Host, _joinedAt.AddHours(1)),
        };

        Assert.True(result.IsSuccess);
        Assert.Equal(status, entry.Status);
        EventAssert.NoneRaised(entry, before);
    }

    [Theory]
    [InlineData(WaitlistStatus.WalkedAway)]
    [InlineData(WaitlistStatus.Removed)]
    public void MarkSeated_WhenEndedOtherwise_ReturnsInvalidTransition(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        EventAssert.InvalidTransition(entry.MarkSeated(BookingId.New(_joinedAt), _joinedAt.AddHours(1)));
        EventAssert.NoneRaised(entry, before);
    }

    [Theory]
    [InlineData(WaitlistStatus.Seated)]
    [InlineData(WaitlistStatus.Removed)]
    public void WalkAway_WhenEndedOtherwise_ReturnsInvalidTransition(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        EventAssert.InvalidTransition(entry.WalkAway(_joinedAt.AddHours(1)));
        EventAssert.NoneRaised(entry, before);
    }

    [Theory]
    [InlineData(WaitlistStatus.Seated)]
    [InlineData(WaitlistStatus.WalkedAway)]
    public void Remove_WhenEndedOtherwise_ReturnsInvalidTransition(WaitlistStatus status)
    {
        var entry = InStatus(status);
        var before = entry.LastEventSequence;

        EventAssert.InvalidTransition(entry.Remove(TestData.Host, _joinedAt.AddHours(1)));
        EventAssert.NoneRaised(entry, before);
    }

    // ---------- Overdue (BR-10) ----------

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void IsOverdue_WhenNotifiedMoreThanTenMinutesAgo_IsTrue_BR10(int minutesSinceNotified, bool expected)
    {
        var entry = InStatus(WaitlistStatus.Notified);

        Assert.Equal(expected, entry.IsOverdue(entry.NotifiedAt!.Value.AddMinutes(minutesSinceNotified), 10));
    }

    [Fact]
    public void IsOverdue_WhenWaiting_IsFalse()
    {
        var entry = Waiting();

        Assert.False(entry.IsOverdue(_joinedAt.AddHours(2), 10));
    }

    private static WaitlistEntry Waiting() =>
        WaitlistEntry.Join(_venueId, TestData.GuestId, TestData.ContactName, PartySize.Of(4), 25, _joinedAt).Value;

    private static WaitlistEntry InStatus(WaitlistStatus status)
    {
        var entry = Waiting();
        var at = _joinedAt.AddMinutes(15);
        switch (status)
        {
            case WaitlistStatus.Waiting:
                break;
            case WaitlistStatus.Notified:
                entry.Notify(TestData.Table4, at);
                break;
            case WaitlistStatus.Seated:
                entry.MarkSeated(BookingId.New(at), at);
                break;
            case WaitlistStatus.WalkedAway:
                entry.WalkAway(at);
                break;
            case WaitlistStatus.Removed:
                entry.Remove(TestData.Host, at);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, entry.Status);
        return entry;
    }
}
