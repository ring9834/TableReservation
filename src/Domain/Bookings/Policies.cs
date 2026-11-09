using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Domain.Bookings;

/// <summary>
/// Refund rule (BR-07, Design §8.3). Called only by <see cref="Booking"/> when it is cancelled, and reads
/// only the terms saved on the booking (§2.6), never the venue's current settings.
/// </summary>
public static class RefundPolicy
{
    public static RefundDecision Decide(Booking booking, CancelReason reason, Actor by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(by);

        // Nothing paid (no deposit, or hold still pending): nothing to refund.
        if (booking.Deposit is not { Status: DepositStatus.Captured })
        {
            return RefundDecision.NotApplicable;
        }

        // Payment arrived after the hold expired and the table was taken (§3.1, §12).
        if (reason == CancelReason.HoldExpired)
        {
            return RefundDecision.Full;
        }

        var rule = booking.Terms.Cancellation;
        if (reason == CancelReason.HostRequest || by.Type == ActorType.Staff)
        {
            return ToDecision(rule.HostCancelRefund);
        }

        var inTime = booking.Period.Start - now >= TimeSpan.FromHours(rule.FreeUntilHoursBefore);
        return inTime ? ToDecision(rule.RefundWhenInTime) : RefundDecision.None;
    }

    private static RefundDecision ToDecision(RefundAmount amount) =>
        amount == RefundAmount.Full ? RefundDecision.Full : RefundDecision.None;
}

/// <summary>Reminder timing (BR-13, Design §7.1).</summary>
public static class ReminderPolicy
{
    /// <summary>A reminder goes out this long before the booking.</summary>
    public static readonly TimeSpan LeadTime = TimeSpan.FromHours(24);

    /// <summary>No reminder when the booking starts within this time.</summary>
    public static readonly TimeSpan MinimumNotice = TimeSpan.FromHours(2);

    /// <summary>
    /// True when a reminder may be sent now: the booking is <c>Booked</c> (not yet confirmed), was made at
    /// least 24 h ahead (otherwise its confirmation carried the manage-booking link), and starts 2 h or more from now.
    /// </summary>
    public static bool CanSend(Booking booking, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return booking.Status == BookingStatus.Booked
            && booking.Period.Start - booking.CreatedAt >= LeadTime
            && booking.Period.Start - now >= MinimumNotice;
    }
}

/// <summary>Why an inbound <c>BillClosed</c> could not be matched (§6.5, §6.6; <c>pos_messages.unmatched_reason</c>).</summary>
public static class UnmatchedReasons
{
    public const string NoMatch = "no_match";
    public const string Ambiguous = "ambiguous";
    public const string SecondBill = "second_bill";
}

/// <summary>The outcome of the late-match rule for one inbound bill.</summary>
public abstract record BillMatch
{
    private BillMatch()
    {
    }

    /// <summary>Steps 1–2: the booking is seated; complete it with <c>Complete(BillClosed, bill)</c>.</summary>
    public sealed record CompleteSeated(Booking Booking) : BillMatch;

    /// <summary>Steps 1 and 3: the booking was cleared by a host without a bill; call <c>AttachLateBill</c>.</summary>
    public sealed record AttachLate(Booking Booking) : BillMatch;

    /// <summary>The bill is already recorded on this booking; ignore it.</summary>
    public sealed record Duplicate(Booking Booking) : BillMatch;

    /// <summary>Step 4: keep the inbound POS message as <c>Unmatched</c> for a manager.</summary>
    public sealed record Unmatched(string Reason) : BillMatch;
}

/// <summary>
/// Late-match rule (Design §6.5): decides which booking an inbound <c>BillClosed</c> belongs to.
/// The handler loads the candidates; this rule never guesses between two of them.
/// </summary>
public static class LateMatchRule
{
    /// <param name="bill">The bill from the POS.</param>
    /// <param name="bookingById">Step 1: the booking named by the bill's <c>bookingId</c>, if any and found.</param>
    /// <param name="seatedAtTable">Step 2: the booking seated at the bill's table, if any.</param>
    /// <param name="completedAtTable">Step 3: bookings at the bill's table completed recently.</param>
    /// <param name="lateBillMatchMinutes">The venue's window (<c>VenueSettings.LateBillMatchMinutes</c>).</param>
    public static BillMatch Match(
        BookingBill bill,
        Booking? bookingById,
        Booking? seatedAtTable,
        IReadOnlyCollection<Booking> completedAtTable,
        int lateBillMatchMinutes)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(completedAtTable);

        var all = new[] { bookingById, seatedAtTable }.OfType<Booking>().Concat(completedAtTable);
        var holder = all.FirstOrDefault(b => b.Bills.Any(x => x.PosBillId == bill.PosBillId));
        if (holder is not null)
        {
            return new BillMatch.Duplicate(holder);
        }

        // Step 1: exact match by booking ID.
        if (bookingById is not null)
        {
            return bookingById switch
            {
                { Status: BookingStatus.Seated } => new BillMatch.CompleteSeated(bookingById),
                { Status: BookingStatus.Completed, Bills.Count: > 0 } => new BillMatch.Unmatched(UnmatchedReasons.SecondBill),
                { Status: BookingStatus.Completed, CompletedBy: CompletionSource.ClearedByHost }
                    when bookingById.IsWithinLateMatchWindow(bill.ClosedAt, lateBillMatchMinutes)
                    => new BillMatch.AttachLate(bookingById),
                _ => new BillMatch.Unmatched(UnmatchedReasons.NoMatch),
            };
        }

        // Step 2: a booking seated at the table.
        if (seatedAtTable is { Status: BookingStatus.Seated })
        {
            return new BillMatch.CompleteSeated(seatedAtTable);
        }

        // Step 3: exactly one booking cleared by a host, without a bill, inside the window.
        var candidates = completedAtTable
            .Where(b => b.Status == BookingStatus.Completed
                && b.CompletedBy == CompletionSource.ClearedByHost
                && b.Bills.Count == 0
                && b.IsWithinLateMatchWindow(bill.ClosedAt, lateBillMatchMinutes))
            .ToList();

        // Step 4: none, or more than one.
        return candidates.Count switch
        {
            1 => new BillMatch.AttachLate(candidates[0]),
            0 => new BillMatch.Unmatched(UnmatchedReasons.NoMatch),
            _ => new BillMatch.Unmatched(UnmatchedReasons.Ambiguous),
        };
    }
}
