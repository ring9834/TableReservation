using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Domain.Bookings;

public enum BookingStatus
{
    PendingPayment,
    Booked,
    Confirmed,
    Seated,
    Completed,
    Cancelled,
}

public enum BookingChannel
{
    Online,
    Phone,
    WalkIn,
}

public enum DepositStatus
{
    Pending,
    Captured,
    Refunded,

    /// <summary>The hold expired, or the booking was cancelled, before payment.</summary>
    Released,
}

public enum CancelReason
{
    GuestRequest,
    HostRequest,
    HoldExpired,
}

public enum CompletionSource
{
    /// <summary>The POS reported the bill paid.</summary>
    BillClosed,

    /// <summary>A host tapped "Clear" (AC4.1).</summary>
    ClearedByHost,
}

/// <summary>The refund decision made once, at cancellation (Design §8.3).</summary>
public enum RefundDecision
{
    Full,
    None,
    NotApplicable,
}

public enum SeatSource
{
    Booking,
    WalkIn,
}

public enum BillMatchedBy
{
    LateMatch,
    Manual,
}

public enum ReminderResponse
{
    Confirmed,
    Cancelled,
}

/// <summary>
/// The venue's rules copied into the booking when it is created (§2.6). Never changed afterwards.
/// </summary>
public sealed record BookingTerms(
    DepositRule Deposit,
    CancellationRule Cancellation,
    int TurnMinutes,
    DateTimeOffset CapturedAt,
    int SettingsVersion)
{
    public static BookingTerms From(VenueSettings settings, int settingsVersion, PartySize partySize, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new BookingTerms(settings.Deposit, settings.Cancellation, settings.TurnMinutesFor(partySize), now, settingsVersion);
    }

    public TimeSpan Turn => TimeSpan.FromMinutes(TurnMinutes);
}

/// <summary>One settled POS bill (§6.6). <c>Booking.BillTotal</c> is the sum of the bills.</summary>
public sealed record BookingBill(string PosBillId, Money Total, Money? Tip, DateTimeOffset ClosedAt, PosMessageId SourceMessage);

/// <summary>A table held by a booking. <see cref="Active"/> mirrors the booking's status (§12).</summary>
public sealed class TableAssignment
{
    internal TableAssignment(TableId tableId, TimeRange period)
    {
        TableId = tableId;
        Period = period;
        Active = true;
    }

    public TableId TableId { get; }

    public TimeRange Period { get; }

    public bool Active { get; private set; }

    internal void Release() => Active = false;
}

/// <summary>The deposit inside a booking (§3.3). Changes only through <see cref="Booking"/>.</summary>
public sealed class Deposit
{
    internal Deposit(Money amount)
    {
        Amount = amount;
        Status = DepositStatus.Pending;
    }

    public Money Amount { get; }

    public DepositStatus Status { get; private set; }

    public string? PaymentIntentId { get; private set; }

    public string? RefundId { get; private set; }

    internal void Capture(string paymentIntentId)
    {
        Status = DepositStatus.Captured;
        PaymentIntentId = paymentIntentId;
    }

    internal void Release() => Status = DepositStatus.Released;

    internal void Refund(string refundId)
    {
        Status = DepositStatus.Refunded;
        RefundId = refundId;
    }
}

/// <summary>The reminder sent for a booking and the guest's response (§2.2).</summary>
public sealed record ReminderRecord(
    DateTimeOffset SentAt,
    string PolicyShown,
    ReminderResponse? Response = null,
    DateTimeOffset? RespondedAt = null);
