using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Domain.Bookings;

/// <summary>
/// Booking aggregate (Design §2.2, state machine §3.1). Owns the deposit, table assignments,
/// bills and reminder record. Any transition not in §3.1 returns <c>invalid_transition</c>;
/// a transition to the current state is a no-op success that raises nothing.
/// </summary>
public sealed class Booking : AggregateRoot<BookingId>
{
    private readonly List<TableAssignment> _tables = [];
    private readonly List<BookingBill> _bills = [];

    /// <summary>For EF Core only.</summary>
    private Booking()
    {
        Reference = null!;
        Terms = null!;
    }

    private Booking(
        BookingId id, VenueId venueId, GuestId? guestId, PersonName? contactName, BookingReference reference, BookingChannel channel,
        PartySize partySize, TimeRange period, BookingTerms terms, string? notes, string? occasion, Language language, DateTimeOffset createdAt)
    {
        Id = id;
        VenueId = venueId;
        GuestId = guestId;
        ContactName = contactName;
        Reference = reference;
        Channel = channel;
        PartySize = partySize;
        Period = period;
        Terms = terms;
        Notes = notes;
        Occasion = occasion;
        Language = language;
        CreatedAt = createdAt;
    }

    public VenueId VenueId { get; }

    public GuestId? GuestId { get; }

    /// <summary>
    /// The name given when this booking was made (M6a). The guest profile keeps its own stored name, so a known mobile
    /// that books under another name shows that name on this booking. <c>null</c> for a walk-in without a name.
    /// </summary>
    public PersonName? ContactName { get; }

    public BookingReference Reference { get; }

    public BookingChannel Channel { get; }

    public PartySize PartySize { get; }

    public TimeRange Period { get; private set; }

    public BookingStatus Status { get; private set; }

    public string? Notes { get; }

    public string? Occasion { get; }

    /// <summary>
    /// The language chosen for this booking (M7a): the guest's language at submit, or for a walk-in seated from the
    /// waitlist the entry's. Messages about the booking go out in it; the guest profile keeps its own.
    /// </summary>
    public Language Language { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? HoldExpiresAt { get; private set; }

    public DateTimeOffset? SeatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public CompletionSource? CompletedBy { get; private set; }

    /// <summary>Why the booking was cancelled; needed for the D7 <c>hold_expired</c> rule.</summary>
    public CancelReason? CancellationReason { get; private set; }

    /// <summary>The refund decision made once, at cancellation (§8.3).</summary>
    public RefundDecision? CancellationRefund { get; private set; }

    public Deposit? Deposit { get; private set; }

    public ReminderRecord? Reminder { get; private set; }

    public BookingTerms Terms { get; }

    public IReadOnlyList<TableAssignment> Tables => _tables;

    public IReadOnlyList<BookingBill> Bills => _bills;

    /// <summary>What the guests spent: the sum of <see cref="Bills"/>, or <c>null</c> before any bill (§6.6).</summary>
    public Money? BillTotal => _bills.Count == 0 ? null : _bills.Select(b => b.Total).Aggregate((a, b) => a + b);

    /// <summary>Active statuses hold a table in the exclusion constraint (§3.1).</summary>
    public bool IsActive => Status is BookingStatus.PendingPayment or BookingStatus.Booked
        or BookingStatus.Confirmed or BookingStatus.Seated;

    private IReadOnlyList<TableId> TableIds => _tables.Select(t => t.TableId).ToList();

    /// <summary>
    /// Online or phone booking (§3.1 rows "→ PendingPayment" and "→ Booked").
    /// Checks the online party limit (BR-04) and the slot (BR-01), copies the terms (§2.6)
    /// and, for online bookings only, applies the deposit rule (BR-06).
    /// </summary>
    /// <param name="holdClockNow">
    /// The infrastructure clock's now (M8a, Design §8.4): the payment hold is measured in real time, so it still runs out
    /// while the demo's business clock stands still. Defaults to <paramref name="now"/>.
    /// </param>
    public static Result<Booking> Create(
        Venue venue, GuestId? guestId, PersonName contactName, BookingReference reference, BookingChannel channel, PartySize partySize,
        DateTimeOffset startsAt, IReadOnlyCollection<TableId> tableIds, string? notes, string? occasion, DateTimeOffset now,
        Language language = Language.En, DateTimeOffset? holdClockNow = null)
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(contactName);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(tableIds);
        if (channel == BookingChannel.WalkIn)
        {
            throw new ArgumentException("Use CreateWalkIn for walk-ins.", nameof(channel));
        }

        var settings = venue.Settings;
        if (channel == BookingChannel.Online && partySize.Value > settings.MaxOnlinePartySize)
        {
            return Error.Rule(ErrorCodes.PartyTooLarge);
        }

        if (startsAt <= now || !settings.IsBookableSlot(startsAt, venue.FindTimeZone()))
        {
            return Error.Rule(ErrorCodes.OutsideServiceHours);
        }

        var tablesCheck = CheckTables(venue, tableIds);
        if (tablesCheck.IsFailure)
        {
            return tablesCheck.Error!;
        }

        var terms = BookingTerms.From(settings, venue.SettingsVersion, partySize, now);
        var booking = new Booking(
            BookingId.New(now), venue.Id, guestId, contactName, reference, channel, partySize,
            TimeRange.FromStart(startsAt, terms.Turn), terms, notes, occasion, language, now);
        booking.AssignTables(tableIds);

        // Deposits apply to online bookings only; phone bookings never start as PendingPayment (§8.2).
        var depositDue = channel == BookingChannel.Online ? terms.Deposit.DueFor(partySize, venue.Currency) : null;
        if (depositDue is { } amount)
        {
            booking.Deposit = new Deposit(amount);
            booking.HoldExpiresAt = (holdClockNow ?? now).AddMinutes(settings.PaymentHoldMinutes);
            booking.Status = BookingStatus.PendingPayment;
        }
        else
        {
            booking.Status = BookingStatus.Booked;
        }

        booking.RaiseCreated(now);
        return booking;
    }

    /// <summary>Walk-in, seated straight away for the turn time (§3.1 row "→ Seated", CreateWalkIn).</summary>
    public static Result<Booking> CreateWalkIn(
        Venue venue, GuestId? guestId, PersonName? contactName, BookingReference reference, PartySize partySize,
        IReadOnlyCollection<TableId> tableIds, WaitlistEntryId? waitlistEntryId, DateTimeOffset now, Language language = Language.En)
    {
        ArgumentNullException.ThrowIfNull(venue);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(tableIds);

        var tablesCheck = CheckTables(venue, tableIds);
        if (tablesCheck.IsFailure)
        {
            return tablesCheck.Error!;
        }

        var terms = BookingTerms.From(venue.Settings, venue.SettingsVersion, partySize, now);
        var booking = new Booking(
            BookingId.New(now), venue.Id, guestId, contactName, reference, BookingChannel.WalkIn, partySize,
            TimeRange.FromStart(now, terms.Turn), terms, notes: null, occasion: null, language, now)
        {
            Status = BookingStatus.Seated,
            SeatedAt = now,
        };
        booking.AssignTables(tableIds);

        booking.RaiseCreated(now);
        booking.Raise(new BookingSeated(
            DomainEvent.NewEventId(now), now, booking.VenueId, booking.Id, booking.TableIds, partySize.Value,
            SeatSource.WalkIn, waitlistEntryId, now));
        return booking;
    }

    /// <summary>
    /// Stripe reported the deposit paid (§3.1 CaptureDeposit rows, D7).
    /// <paramref name="tableStillFree"/> is the handler's re-check of the table; it matters only once the hold has expired.
    /// The hold is compared with <paramref name="holdClockNow"/> (infrastructure clock, M8a); <paramref name="now"/> is the
    /// business-clock time of the event.
    /// </summary>
    public Result CaptureDeposit(
        string paymentIntentId, Money amount, bool tableStillFree, DateTimeOffset now, DateTimeOffset? holdClockNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        switch (Status)
        {
            case BookingStatus.Cancelled when CancellationReason == CancelReason.HoldExpired:
                return Error.Conflict(ErrorCodes.HoldExpired);

            case BookingStatus.Booked when Deposit is { Status: DepositStatus.Captured } d && d.PaymentIntentId == paymentIntentId:
                return Result.Success();

            case BookingStatus.PendingPayment:
                if (Deposit!.PaymentIntentId is { } attached && attached != paymentIntentId)
                {
                    return Error.Rule(ErrorCodes.PaymentIntentMismatch);
                }

                if (amount != Deposit.Amount)
                {
                    return Error.Rule(ErrorCodes.DepositAmountMismatch);
                }

                Deposit.Capture(paymentIntentId);
                var holdExpired = HoldExpiresAt is { } expiry && (holdClockNow ?? now) >= expiry;
                if (!holdExpired || tableStillFree)
                {
                    Status = BookingStatus.Booked;
                    HoldExpiresAt = null;
                    Raise(new DepositCaptured(DomainEvent.NewEventId(now), now, VenueId, Id, paymentIntentId, amount.Cents));
                    return Result.Success();
                }

                var actor = Actor.ForSystem();
                CancelCore(actor, CancelReason.HoldExpired, RefundPolicy.Decide(this, CancelReason.HoldExpired, actor, now), now);
                return Result.Success();

            default:
                return Error.InvalidTransition;
        }
    }

    /// <summary>
    /// The payment intent created for the deposit (M8a, Design §8.4). Stored so <c>charge.refunded</c> can find the booking
    /// and a payment for another intent is refused. Raises no event. The same ID again is a no-op; another one is a conflict.
    /// </summary>
    public Result AttachPaymentIntent(string paymentIntentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        if (Deposit is null)
        {
            return Error.InvalidTransition;
        }

        if (Deposit.PaymentIntentId is { } attached)
        {
            return attached == paymentIntentId ? Result.Success() : Error.Rule(ErrorCodes.PaymentIntentMismatch);
        }

        Deposit.AttachIntent(paymentIntentId);
        return Result.Success();
    }

    /// <summary>
    /// A payment that arrived after the hold expiry had already cancelled the booking (M8a, Design §8.4). The booking stays
    /// <c>Cancelled</c>; the deposit records the money Stripe now holds (<c>Released → Captured</c>), and
    /// <see cref="LatePaymentReceived"/> asks for the refund through the outbox, so <c>charge.refunded</c> later marks
    /// the deposit <c>Refunded</c>. The same payment again is a no-op.
    /// </summary>
    public Result RecordLatePayment(string paymentIntentId, Money amount, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentIntentId);

        if (Status != BookingStatus.Cancelled || CancellationReason != CancelReason.HoldExpired || Deposit is null)
        {
            return Error.InvalidTransition;
        }

        if (Deposit.Status is DepositStatus.Captured or DepositStatus.Refunded)
        {
            return Deposit.PaymentIntentId == paymentIntentId ? Result.Success() : Error.Rule(ErrorCodes.PaymentIntentMismatch);
        }

        if (Deposit.PaymentIntentId is { } attached && attached != paymentIntentId)
        {
            return Error.Rule(ErrorCodes.PaymentIntentMismatch);
        }

        if (amount != Deposit.Amount)
        {
            return Error.Rule(ErrorCodes.DepositAmountMismatch);
        }

        Deposit.Capture(paymentIntentId);
        Raise(new LatePaymentReceived(DomainEvent.NewEventId(now), now, VenueId, Id, paymentIntentId, amount.Cents));
        return Result.Success();
    }

    /// <summary>
    /// The payment hold ended before payment (§3.1 ExpireHold). The hold is compared with <paramref name="holdClockNow"/>
    /// (infrastructure clock, M8a); <paramref name="now"/> is the business-clock time of the event.
    /// </summary>
    public Result ExpireHold(DateTimeOffset now, DateTimeOffset? holdClockNow = null)
    {
        if (Status == BookingStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status != BookingStatus.PendingPayment || HoldExpiresAt is not { } expiry || (holdClockNow ?? now) < expiry)
        {
            return Error.InvalidTransition;
        }

        var actor = Actor.ForSystem();
        CancelCore(actor, CancelReason.HoldExpired, RefundPolicy.Decide(this, CancelReason.HoldExpired, actor, now), now);
        return Result.Success();
    }

    /// <summary>The guest confirmed from the reminder (§3.1 Confirm).</summary>
    public Result Confirm(Actor by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (Status == BookingStatus.Confirmed)
        {
            return Result.Success();
        }

        if (Status != BookingStatus.Booked || now >= Period.Start)
        {
            return Error.InvalidTransition;
        }

        Status = BookingStatus.Confirmed;
        if (Reminder is not null)
        {
            Reminder = Reminder with { Response = ReminderResponse.Confirmed, RespondedAt = now };
        }

        Raise(new BookingConfirmed(DomainEvent.NewEventId(now), now, VenueId, Id, by));
        return Result.Success();
    }

    /// <summary>
    /// Guest or host cancels (§3.1 Cancel). The refund decision is made here, once (§8.3), and carried by
    /// <see cref="BookingCancelled"/>. Cancelling again returns the stored decision and raises nothing.
    /// </summary>
    public Result<RefundDecision> Cancel(Actor by, CancelReason reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (Status == BookingStatus.Cancelled)
        {
            return CancellationRefund ?? RefundDecision.NotApplicable;
        }

        if (Status is not (BookingStatus.PendingPayment or BookingStatus.Booked or BookingStatus.Confirmed))
        {
            return Error.InvalidTransition;
        }

        var decision = RefundPolicy.Decide(this, reason, by, now);
        CancelCore(by, reason, decision, now);
        return decision;
    }

    /// <summary>
    /// Host seats the party (§3.1 Seat). The period restarts now for the turn time. A table the booking keeps has its
    /// assignment moved to the new period in place; only tables that change are removed or added, so the save never
    /// depends on EF ordering a delete before an insert on the same table (exclusion constraint, Design §14.2).
    /// </summary>
    public Result Seat(IReadOnlyCollection<TableId> tableIds, DateTimeOffset now, Actor by)
    {
        ArgumentNullException.ThrowIfNull(tableIds);
        ArgumentNullException.ThrowIfNull(by);

        if (Status == BookingStatus.Seated)
        {
            return Result.Success();
        }

        if (Status is not (BookingStatus.Booked or BookingStatus.Confirmed))
        {
            return Error.InvalidTransition;
        }

        if (tableIds.Count == 0)
        {
            throw new ArgumentException("At least one table is required.", nameof(tableIds));
        }

        Status = BookingStatus.Seated;
        SeatedAt = now;
        Period = TimeRange.FromStart(now, Terms.Turn);
        var wanted = tableIds.Distinct().ToList();
        _tables.RemoveAll(t => !wanted.Contains(t.TableId));
        foreach (var kept in _tables)
        {
            kept.MoveTo(Period);
        }

        AssignTables(wanted.Where(id => _tables.TrueForAll(t => t.TableId != id)));

        Raise(new BookingSeated(
            DomainEvent.NewEventId(now), now, VenueId, Id, TableIds, PartySize.Value, SeatSource.Booking, null, now));
        return Result.Success();
    }

    /// <summary>
    /// The party left (§3.1 Complete): <see cref="CompletionSource.BillClosed"/> with the POS bill,
    /// or <see cref="CompletionSource.ClearedByHost"/> without one.
    /// </summary>
    public Result Complete(CompletionSource source, BookingBill? bill, DateTimeOffset now)
    {
        if (source == CompletionSource.BillClosed && bill is null)
        {
            throw new ArgumentException("A bill is required when the POS closed the bill.", nameof(bill));
        }

        if (source == CompletionSource.ClearedByHost && bill is not null)
        {
            throw new ArgumentException("A host clear carries no bill.", nameof(bill));
        }

        if (Status == BookingStatus.Completed)
        {
            return Result.Success();
        }

        if (Status != BookingStatus.Seated)
        {
            return Error.InvalidTransition;
        }

        Status = BookingStatus.Completed;
        CompletedAt = now;
        CompletedBy = source;
        if (bill is not null)
        {
            _bills.Add(bill);
        }

        ReleaseTables();
        Raise(new BookingCompleted(
            DomainEvent.NewEventId(now), now, VenueId, Id, source, bill?.PosBillId, bill?.Total.Cents, bill?.ClosedAt ?? now));
        Raise(new TableCleared(DomainEvent.NewEventId(now), now, VenueId, Id, TableIds, now));
        return Result.Success();
    }

    /// <summary>
    /// A POS bill that arrived after the host cleared the table (§3.1 AttachLateBill, §6.5), or one matched by a manager.
    /// </summary>
    public Result AttachLateBill(BookingBill bill, BillMatchedBy matchedBy, int lateBillMatchMinutes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bill);

        if (Status != BookingStatus.Completed || CompletedBy != CompletionSource.ClearedByHost)
        {
            return Error.InvalidTransition;
        }

        if (_bills.Any(b => b.PosBillId == bill.PosBillId))
        {
            return Result.Success();
        }

        if (_bills.Count > 0)
        {
            return Error.Rule(ErrorCodes.SecondBill);
        }

        if (matchedBy == BillMatchedBy.LateMatch && !IsWithinLateMatchWindow(bill.ClosedAt, lateBillMatchMinutes))
        {
            return Error.Rule(ErrorCodes.OutsideLateMatchWindow);
        }

        _bills.Add(bill);
        Raise(new BookingBillAttached(
            DomainEvent.NewEventId(now), now, VenueId, Id, bill.PosBillId, bill.Total.Cents, bill.SourceMessage, matchedBy, now));
        return Result.Success();
    }

    /// <summary>Stripe confirmed a refund (§8.1 RecordRefundHandler).</summary>
    public Result RecordRefund(string refundId, Money amount, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refundId);

        if (Deposit is { Status: DepositStatus.Refunded })
        {
            return Result.Success();
        }

        if (Deposit is not { Status: DepositStatus.Captured })
        {
            return Error.InvalidTransition;
        }

        Deposit.Refund(refundId);
        Raise(new DepositRefunded(DomainEvent.NewEventId(now), now, VenueId, Id, refundId, amount.Cents));
        return Result.Success();
    }

    /// <summary>A reminder was sent (§7.1, BR-13). Records the cancellation wording the guest saw. No event.</summary>
    public Result RecordReminder(DateTimeOffset sentAt, string policyShown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyShown);

        if (Reminder is not null)
        {
            return Result.Success();
        }

        if (!ReminderPolicy.CanSend(this, sentAt))
        {
            return Error.Rule(ErrorCodes.ReminderNotAllowed);
        }

        Reminder = new ReminderRecord(sentAt, policyShown);
        return Result.Success();
    }

    /// <summary>
    /// BR-13 warning: cancelling now would keep the paid deposit, because the free-cancellation window
    /// in <see cref="Terms"/> has passed.
    /// </summary>
    public bool CancellingNowKeepsDeposit(DateTimeOffset now) =>
        Status is BookingStatus.Booked or BookingStatus.Confirmed
        && Deposit is { Status: DepositStatus.Captured }
        && RefundPolicy.Decide(this, CancelReason.GuestRequest, Actor.ForGuest(GuestId), now) == RefundDecision.None;

    /// <summary>|closedAt − CompletedAt| ≤ window (§6.5): bills closed shortly before or after the host cleared the table.</summary>
    public bool IsWithinLateMatchWindow(DateTimeOffset closedAt, int lateBillMatchMinutes) =>
        CompletedAt is { } completedAt && (closedAt - completedAt).Duration() <= TimeSpan.FromMinutes(lateBillMatchMinutes);

    private static Result CheckTables(Venue venue, IReadOnlyCollection<TableId> tableIds)
    {
        if (tableIds.Count == 0)
        {
            return Error.Validation("tableIds", "tables_required");
        }

        return tableIds.All(id => venue.FindTable(id) is not null) ? Result.Success() : Error.NotFound();
    }

    private void AssignTables(IEnumerable<TableId> tableIds)
    {
        foreach (var tableId in tableIds.Distinct())
        {
            _tables.Add(new TableAssignment(tableId, Period));
        }
    }

    private void ReleaseTables()
    {
        foreach (var table in _tables)
        {
            table.Release();
        }
    }

    private void CancelCore(Actor by, CancelReason reason, RefundDecision decision, DateTimeOffset now)
    {
        Status = BookingStatus.Cancelled;
        CancellationReason = reason;
        CancellationRefund = decision;
        if (Deposit is { Status: DepositStatus.Pending })
        {
            Deposit.Release();
        }

        if (Reminder is { Response: null } && by.Type == ActorType.Guest)
        {
            Reminder = Reminder with { Response = ReminderResponse.Cancelled, RespondedAt = now };
        }

        ReleaseTables();
        Raise(new BookingCancelled(DomainEvent.NewEventId(now), now, VenueId, Id, reason, by, decision));
    }

    private void RaiseCreated(DateTimeOffset now) =>
        Raise(new BookingCreated(
            DomainEvent.NewEventId(now), now, VenueId, Id, GuestId, Reference.Value, Channel, PartySize.Value,
            Period.Start, Period.End, TableIds, Deposit?.Amount.Cents, HoldExpiresAt));
}
