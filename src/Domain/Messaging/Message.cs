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
/// Message aggregate (Design §2.2, state machine §3.4, delivery rules §7.1). Handlers queue it;
/// the delivery worker sends it, retries with backoff and expires stale ones.
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
        string body, List<MessageAction> actions, Guid subjectId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
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
        NextAttemptAt = createdAt;
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

    public string? ProviderRef { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Queue a message. Expiry by purpose (§7.1): <c>TableReady</c> 10 minutes after creation;
    /// <c>Reminder</c> at the booking start (<paramref name="bookingStart"/> required); others 24 hours.
    /// </summary>
    public static Message Queue(
        VenueId venueId, GuestId guestId, MessagePurpose purpose, MessageChannel channel, Language language,
        string body, IEnumerable<MessageAction> actions, Guid subjectId, DateTimeOffset now, DateTimeOffset? bookingStart = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentNullException.ThrowIfNull(actions);

        var expiresAt = purpose switch
        {
            MessagePurpose.TableReady => now.AddMinutes(10),
            MessagePurpose.Reminder => bookingStart
                ?? throw new ArgumentException("A reminder expires at the booking start.", nameof(bookingStart)),
            _ => now.AddHours(24),
        };

        return new Message(MessageId.New(now), venueId, guestId, purpose, channel, language, body, [.. actions], subjectId, now, expiresAt);
    }

    /// <summary>The provider accepted the message (§3.4 Queued → Sent). Raises <see cref="MessageSent"/>.</summary>
    public Result MarkSent(string providerRef, string toMasked, DateTimeOffset at)
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
        return Result.Success();
    }

    /// <summary>
    /// A temporary provider error (§3.4 note, §7.1): attempts + 1 and retry after the backoff.
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
        return Result.Success();
    }
}
