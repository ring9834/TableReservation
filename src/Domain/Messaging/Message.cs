using Reservation.Domain.Common;

namespace Reservation.Domain.Messaging;

public enum MessagePurpose
{
    Confirmation,
    Reminder,
    TableReady,
    Cancellation,
    Refund,
}

public enum MessageStatus
{
    Queued,
    Sent,
    Delivered,
    Failed,
}

public enum MessageChannel
{
    Sms,
    Email,
}

/// <summary>A button in a message, such as Confirm or Cancel, backed by a signed guest link (§7).</summary>
public sealed record MessageAction(string Kind, string Url);

[EventMetadata("MessageSent", Version = 1, AggregateType = "Message")]
public sealed record MessageSent(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    MessageId MessageId, MessagePurpose Purpose, MessageChannel Channel, Guid SubjectId, string ToMasked)
    : DomainEvent(EventId, OccurredAt, VenueId);

/// <summary>
/// Message aggregate (Design §2.2, state machine §3.4, delivery rules §7.1, §7.2). Handlers queue it;
/// the delivery worker sends it, retries with backoff and expires stale ones.
/// Two clocks (§5.6): <see cref="CreatedAt"/>, <see cref="SentAt"/> and <see cref="DeliveredAt"/> are business-clock
/// times people see; <see cref="NextAttemptAt"/>, <see cref="ExpiresAt"/> and <see cref="DeliveryDueAt"/> are
/// infrastructure-clock times the worker schedules by, so texts still go out while the demo clock stands still.
/// </summary>
public sealed class Message : AggregateRoot<MessageId>
{
    /// <summary>Retry delays after attempts 1, 2, 3, then every 10 minutes (§7.1).</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    private readonly List<MessageAction> _actions;

    /// <summary>For EF Core only.</summary>
    private Message()
    {
        _actions = [];
        Body = null!;
    }

    private Message(
        MessageId id, VenueId venueId, GuestId guestId, MessagePurpose purpose, MessageChannel channel, Language language,
        string body, List<MessageAction> actions, Guid subjectId, DateTimeOffset createdAt, DateTimeOffset queuedAt, DateTimeOffset expiresAt)
    {
        Id = id;
        VenueId = venueId;
        GuestId = guestId;
        Purpose = purpose;
        Channel = channel;
        Language = language;
        Body = body;
        _actions = actions;
        SubjectId = subjectId;
        CreatedAt = createdAt;
        NextAttemptAt = queuedAt;
        ExpiresAt = expiresAt;
        Status = MessageStatus.Queued;
    }

    public VenueId VenueId { get; }

    public GuestId GuestId { get; }

    public MessagePurpose Purpose { get; }

    public MessageChannel Channel { get; }

    public Language Language { get; }

    public string Body { get; }

    public IReadOnlyList<MessageAction> Actions => _actions;

    /// <summary>The booking or waitlist entry the message is about.</summary>
    public Guid SubjectId { get; }

    public MessageStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// When a sent message is to be marked delivered by the worker (infrastructure clock), for a sender that reports
    /// delivery itself after a delay (the demo phone panel). <c>null</c> otherwise.
    /// </summary>
    public DateTimeOffset? DeliveryDueAt { get; private set; }

    public string? ProviderRef { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Queue a message. <paramref name="now"/> is the business clock (<see cref="CreatedAt"/>);
    /// <paramref name="queuedAt"/> the infrastructure clock, which the first attempt and the expiry count from.
    /// Expiry by purpose (§7.1): <c>TableReady</c> 10 minutes; <c>Reminder</c> at the booking start
    /// (<paramref name="bookingStart"/> required, a business-clock time, so the time left until it is added); others 24 hours.
    /// </summary>
    public static Message Queue(
        VenueId venueId, GuestId guestId, MessagePurpose purpose, MessageChannel channel, Language language,
        string body, IEnumerable<MessageAction> actions, Guid subjectId, DateTimeOffset now, DateTimeOffset queuedAt,
        DateTimeOffset? bookingStart = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentNullException.ThrowIfNull(actions);

        var lifetime = purpose switch
        {
            MessagePurpose.TableReady => TimeSpan.FromMinutes(10),
            MessagePurpose.Reminder => bookingStart is { } start
                ? (start > now ? start - now : TimeSpan.Zero)
                : throw new ArgumentException("A reminder expires at the booking start.", nameof(bookingStart)),
            _ => TimeSpan.FromHours(24),
        };

        // The ID is a UUID v7 on the infrastructure clock, so ordering by ID follows real time even while the demo clock stands still.
        return new Message(
            MessageId.New(queuedAt), venueId, guestId, purpose, channel, language, body, [.. actions], subjectId,
            now, queuedAt, queuedAt + lifetime);
    }

    /// <summary>Past its expiry on the infrastructure clock (§7.1): it fails instead of being sent.</summary>
    public bool IsExpired(DateTimeOffset infrastructureNow) => infrastructureNow >= ExpiresAt;

    /// <summary>
    /// The provider accepted the message (§3.4 Queued → Sent). Raises <see cref="MessageSent"/>. <paramref name="at"/> is
    /// the business clock; <paramref name="deliveryDueAt"/> (infrastructure clock) is set when the sender reports
    /// delivery itself after a delay, so the worker marks it delivered then.
    /// </summary>
    public Result MarkSent(string providerRef, string toMasked, DateTimeOffset at, DateTimeOffset? deliveryDueAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(toMasked);

        if (Status == MessageStatus.Sent)
        {
            return Result.Success();
        }

        if (Status != MessageStatus.Queued)
        {
            return Error.InvalidTransition;
        }

        Status = MessageStatus.Sent;
        Attempts++;
        SentAt = at;
        ProviderRef = providerRef;
        LastError = null;
        DeliveryDueAt = deliveryDueAt;
        Raise(new MessageSent(DomainEvent.NewEventId(at), at, VenueId, Id, Purpose, Channel, SubjectId, toMasked));
        return Result.Success();
    }

    /// <summary>Delivery report (§3.4 Sent → Delivered).</summary>
    public Result MarkDelivered(DateTimeOffset at)
    {
        if (Status == MessageStatus.Delivered)
        {
            return Result.Success();
        }

        if (Status != MessageStatus.Sent)
        {
            return Error.InvalidTransition;
        }

        Status = MessageStatus.Delivered;
        DeliveredAt = at;
        DeliveryDueAt = null;
        return Result.Success();
    }

    /// <summary>
    /// A temporary provider error (§3.4 note, §7.1): attempts + 1 and retry after the backoff, on the infrastructure clock.
    /// When the next attempt would be at or after <see cref="ExpiresAt"/>, the message fails instead.
    /// </summary>
    public Result ScheduleRetry(string error, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        if (Status != MessageStatus.Queued)
        {
            return Error.InvalidTransition;
        }

        Attempts++;
        LastError = error;
        var next = now + Backoff[Math.Min(Attempts, Backoff.Count) - 1];
        if (next >= ExpiresAt)
        {
            Status = MessageStatus.Failed;
            return Result.Success();
        }

        NextAttemptAt = next;
        return Result.Success();
    }

    /// <summary>A permanent error, retries used up, or a failed delivery report (§3.4 Queued/Sent → Failed).</summary>
    public Result MarkFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status == MessageStatus.Failed)
        {
            return Result.Success();
        }

        if (Status is not (MessageStatus.Queued or MessageStatus.Sent))
        {
            return Error.InvalidTransition;
        }

        Status = MessageStatus.Failed;
        LastError = reason;
        DeliveryDueAt = null;
        return Result.Success();
    }
}
