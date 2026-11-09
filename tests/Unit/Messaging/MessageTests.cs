using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Messaging;

/// <summary>Message aggregate: Design §3.4 transitions and the §7.1 delivery rules.</summary>
public class MessageTests
{
    private static readonly DateTimeOffset _now = TestData.Start.AddMinutes(-30);
    private static readonly VenueId _venueId = new(Guid.CreateVersion7(TestData.Created));

    // ---------- Queue and expiry by purpose ----------

    [Fact]
    public void Queue_IsQueuedForImmediateDelivery_AndRaisesNothing()
    {
        var message = Queue(MessagePurpose.Confirmation);

        Assert.Equal(MessageStatus.Queued, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(_now, message.NextAttemptAt);
        EventAssert.NoneRaised(message, 0);
    }

    [Theory]
    [InlineData(MessagePurpose.TableReady, 10)]
    [InlineData(MessagePurpose.Confirmation, 24 * 60)]
    [InlineData(MessagePurpose.Cancellation, 24 * 60)]
    [InlineData(MessagePurpose.Refund, 24 * 60)]
    public void Queue_SetsExpiryByPurpose(MessagePurpose purpose, int minutes)
    {
        Assert.Equal(_now.AddMinutes(minutes), Queue(purpose).ExpiresAt);
    }

    [Fact]
    public void Queue_Reminder_ExpiresAtBookingStart()
    {
        var message = Message.Queue(
            _venueId, TestData.GuestId, MessagePurpose.Reminder, MessageChannel.Sms, Language.Zh, "body",
            [new MessageAction("confirm", "https://example.test/c"), new MessageAction("cancel", "https://example.test/x")],
            Guid.CreateVersion7(_now), _now, bookingStart: TestData.Start);

        Assert.Equal(TestData.Start, message.ExpiresAt);
        Assert.Equal(2, message.Actions.Count);
    }

    [Fact]
    public void Queue_ReminderWithoutBookingStart_Throws()
    {
        Assert.Throws<ArgumentException>(() => Queue(MessagePurpose.Reminder));
    }

    // ---------- Queued → Sent ----------

    [Fact]
    public void MarkSent_WhenQueued_IsSentAndRaisesMessageSent()
    {
        var message = Queue(MessagePurpose.TableReady);
        var at = _now.AddSeconds(1);

        var result = message.MarkSent("prov-1", "+64******567", at);

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal(at, message.SentAt);
        Assert.Equal("prov-1", message.ProviderRef);
        Assert.Equal(1, message.Attempts);
        var sent = EventAssert.RaisedOnce<MessageSent>(message, 0);
        Assert.Equal(message.Id, sent.MessageId);
        Assert.Equal(MessagePurpose.TableReady, sent.Purpose);
        Assert.Equal(MessageChannel.Sms, sent.Channel);
        Assert.Equal(message.SubjectId, sent.SubjectId);
        Assert.Equal("+64******567", sent.ToMasked);
    }

    [Fact]
    public void MarkSent_WhenAlreadySent_IsNoOp()
    {
        var message = Queue(MessagePurpose.Confirmation);
        message.MarkSent("prov-1", "+64******567", _now);
        var before = message.LastEventSequence;

        Assert.True(message.MarkSent("prov-1", "+64******567", _now.AddSeconds(1)).IsSuccess);
        EventAssert.NoneRaised(message, before);
    }

    [Theory]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Failed)]
    public void MarkSent_WhenDeliveredOrFailed_ReturnsInvalidTransition(MessageStatus status)
    {
        var message = InStatus(status);
        var before = message.LastEventSequence;

        EventAssert.InvalidTransition(message.MarkSent("prov-2", "+64******567", _now.AddMinutes(1)));
        Assert.Equal(status, message.Status);
        EventAssert.NoneRaised(message, before);
    }

    // ---------- Sent → Delivered ----------

    [Fact]
    public void MarkDelivered_WhenSent_IsDelivered()
    {
        var message = InStatus(MessageStatus.Sent);
        var before = message.LastEventSequence;

        Assert.True(message.MarkDelivered(_now.AddSeconds(2)).IsSuccess);

        Assert.Equal(MessageStatus.Delivered, message.Status);
        Assert.Equal(_now.AddSeconds(2), message.DeliveredAt);
        EventAssert.NoneRaised(message, before);
    }

    [Fact]
    public void MarkDelivered_WhenAlreadyDelivered_IsNoOp()
    {
        var message = InStatus(MessageStatus.Delivered);

        Assert.True(message.MarkDelivered(_now.AddMinutes(1)).IsSuccess);
    }

    [Theory]
    [InlineData(MessageStatus.Queued)]
    [InlineData(MessageStatus.Failed)]
    public void MarkDelivered_WhenNotSent_ReturnsInvalidTransition(MessageStatus status)
    {
        var message = InStatus(status);

        EventAssert.InvalidTransition(message.MarkDelivered(_now.AddMinutes(1)));
        Assert.Equal(status, message.Status);
    }

    // ---------- Retries and failure ----------

    [Fact]
    public void ScheduleRetry_WhenQueued_BacksOff5s30s2m10mThenEvery10m()
    {
        var message = Queue(MessagePurpose.Confirmation);
        int[] expectedSeconds = [5, 30, 120, 600, 600];

        foreach (var (seconds, attempt) in expectedSeconds.Select((s, i) => (s, i + 1)))
        {
            var now = _now.AddMinutes(attempt * 20);
            Assert.True(message.ScheduleRetry("timeout", now).IsSuccess);
            Assert.Equal(attempt, message.Attempts);
            Assert.Equal(now.AddSeconds(seconds), message.NextAttemptAt);
            Assert.Equal("timeout", message.LastError);
            Assert.Equal(MessageStatus.Queued, message.Status);
        }
    }

    [Fact]
    public void ScheduleRetry_WhenNextAttemptWouldBePastExpiry_Fails()
    {
        var message = Queue(MessagePurpose.TableReady);

        message.ScheduleRetry("timeout", _now.AddMinutes(9).AddSeconds(56));

        Assert.Equal(MessageStatus.Failed, message.Status);
    }

    [Theory]
    [InlineData(MessageStatus.Sent)]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Failed)]
    public void ScheduleRetry_WhenNotQueued_ReturnsInvalidTransition(MessageStatus status)
    {
        var message = InStatus(status);

        EventAssert.InvalidTransition(message.ScheduleRetry("timeout", _now.AddMinutes(1)));
        Assert.Equal(status, message.Status);
    }

    [Theory]
    [InlineData(MessageStatus.Queued)]
    [InlineData(MessageStatus.Sent)]
    public void MarkFailed_WhenQueuedOrSent_Fails(MessageStatus status)
    {
        var message = InStatus(status);

        Assert.True(message.MarkFailed("number unreachable").IsSuccess);

        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal("number unreachable", message.LastError);
    }

    [Fact]
    public void MarkFailed_WhenAlreadyFailed_IsNoOp()
    {
        var message = InStatus(MessageStatus.Failed);

        Assert.True(message.MarkFailed("again").IsSuccess);
    }

    [Fact]
    public void MarkFailed_WhenDelivered_ReturnsInvalidTransition()
    {
        var message = InStatus(MessageStatus.Delivered);

        EventAssert.InvalidTransition(message.MarkFailed("late report"));
        Assert.Equal(MessageStatus.Delivered, message.Status);
    }

    private static Message Queue(MessagePurpose purpose) =>
        Message.Queue(_venueId, TestData.GuestId, purpose, MessageChannel.Sms, Language.En, "body", [], Guid.CreateVersion7(_now), _now);

    private static Message InStatus(MessageStatus status)
    {
        var message = Queue(MessagePurpose.Confirmation);
        switch (status)
        {
            case MessageStatus.Queued:
                break;
            case MessageStatus.Sent:
                message.MarkSent("prov-1", "+64******567", _now);
                break;
            case MessageStatus.Delivered:
                message.MarkSent("prov-1", "+64******567", _now);
                message.MarkDelivered(_now.AddSeconds(1));
                break;
            case MessageStatus.Failed:
                message.MarkFailed("number unreachable");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, message.Status);
        return message;
    }
}
