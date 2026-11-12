using Reservation.Domain.Common;

namespace Reservation.Domain.Waitlist;

public enum WaitlistStatus
{
    Waiting,
    Notified,
    Seated,
    WalkedAway,
    Removed,
}

public enum WaitlistOutcome
{
    Seated,
    WalkedAway,
    Removed,
}

[EventMetadata("WaitlistJoined", Version = 1, AggregateType = "WaitlistEntry")]
public sealed record WaitlistJoined(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    WaitlistEntryId WaitlistEntryId, GuestId GuestId, int PartySize, int QuotedMinutes)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("WaitlistNotified", Version = 1, AggregateType = "WaitlistEntry")]
public sealed record WaitlistNotified(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    WaitlistEntryId WaitlistEntryId, TableId TableId, DateTimeOffset NotifiedAt)
    : DomainEvent(EventId, OccurredAt, VenueId);

[EventMetadata("WaitlistEnded", Version = 1, AggregateType = "WaitlistEntry")]
public sealed record WaitlistEnded(
    Guid EventId, DateTimeOffset OccurredAt, VenueId VenueId,
    WaitlistEntryId WaitlistEntryId, WaitlistOutcome Outcome, BookingId? BookingId)
    : DomainEvent(EventId, OccurredAt, VenueId);

/// <summary>Waitlist entry aggregate (Design §2.2, state machine §3.2).</summary>
public sealed class WaitlistEntry : AggregateRoot<WaitlistEntryId>
{
    public const int MaxQuotedWaitMinutes = 240;

    /// <summary>For EF Core only.</summary>
    private WaitlistEntry()
    {
        ContactName = null!;
    }

    private WaitlistEntry(
        WaitlistEntryId id, VenueId venueId, GuestId guestId, PersonName contactName, PartySize partySize, int quotedWaitMinutes, DateTimeOffset joinedAt)
    {
        Id = id;
        VenueId = venueId;
        GuestId = guestId;
        ContactName = contactName;
        PartySize = partySize;
        QuotedWaitMinutes = quotedWaitMinutes;
        JoinedAt = joinedAt;
        Status = WaitlistStatus.Waiting;
    }

    public VenueId VenueId { get; }

    public GuestId GuestId { get; }

    /// <summary>The name given when the party joined (M6a); the guest profile keeps its stored name.</summary>
    public PersonName ContactName { get; }

    public PartySize PartySize { get; }

    public int QuotedWaitMinutes { get; }

    public DateTimeOffset JoinedAt { get; }

    public DateTimeOffset? NotifiedAt { get; private set; }

    public TableId? NotifiedTableId { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public BookingId? BookingId { get; private set; }

    public WaitlistStatus Status { get; private set; }

    public bool IsActive => Status is WaitlistStatus.Waiting or WaitlistStatus.Notified;

    /// <summary>Join the waitlist (§3.2): party 1–20 (by type), quoted wait 0–240 minutes.</summary>
    public static Result<WaitlistEntry> Join(
        VenueId venueId, GuestId guestId, PersonName contactName, PartySize partySize, int quotedWaitMinutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(contactName);
        if (quotedWaitMinutes is < 0 or > MaxQuotedWaitMinutes)
        {
            return Error.Validation("quotedWaitMinutes", "quoted_wait_out_of_range");
        }

        var entry = new WaitlistEntry(WaitlistEntryId.New(now), venueId, guestId, contactName, partySize, quotedWaitMinutes, now);
        entry.Raise(new WaitlistJoined(
            DomainEvent.NewEventId(now), now, venueId, entry.Id, guestId, partySize.Value, quotedWaitMinutes));
        return entry;
    }

    /// <summary>
    /// Overdue (AC4.6, BR-10) is computed, not a state: notified more than <paramref name="timeoutMinutes"/> ago and not seated.
    /// </summary>
    public bool IsOverdue(DateTimeOffset now, int timeoutMinutes) =>
        Status == WaitlistStatus.Notified && NotifiedAt is { } at && now - at > TimeSpan.FromMinutes(timeoutMinutes);

    /// <summary>Table ready. Re-notifying is allowed: it updates <see cref="NotifiedAt"/> and raises again (§3.2).</summary>
    public Result Notify(TableId tableId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.InvalidTransition;
        }

        Status = WaitlistStatus.Notified;
        NotifiedAt = now;
        NotifiedTableId = tableId;
        Raise(new WaitlistNotified(DomainEvent.NewEventId(now), now, VenueId, Id, tableId, now));
        return Result.Success();
    }

    /// <summary>Seated; the walk-in booking is created in the same transaction (§3.2).</summary>
    public Result MarkSeated(BookingId bookingId, DateTimeOffset now) =>
        End(WaitlistStatus.Seated, WaitlistOutcome.Seated, bookingId, now);

    public Result WalkAway(DateTimeOffset now) =>
        End(WaitlistStatus.WalkedAway, WaitlistOutcome.WalkedAway, null, now);

    public Result Remove(Actor by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);
        return End(WaitlistStatus.Removed, WaitlistOutcome.Removed, null, now);
    }

    private Result End(WaitlistStatus target, WaitlistOutcome outcome, BookingId? bookingId, DateTimeOffset now)
    {
        if (Status == target)
        {
            return Result.Success();
        }

        if (!IsActive)
        {
            return Error.InvalidTransition;
        }

        Status = target;
        EndedAt = now;
        BookingId = bookingId;
        Raise(new WaitlistEnded(DomainEvent.NewEventId(now), now, VenueId, Id, outcome, bookingId));
        return Result.Success();
    }
}
