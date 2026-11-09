using Reservation.Domain.Common;

namespace Reservation.Domain.Venues;

/// <summary>Turn time for a range of party sizes (BR-02).</summary>
public sealed record TurnTimeRule(int MinPartySize, int MaxPartySize, int Minutes)
{
    public bool Applies(PartySize partySize) =>
        partySize.Value >= MinPartySize && partySize.Value <= MaxPartySize;
}

/// <summary>
/// Deposit rule (BR-06, Design §8.2): parties of <see cref="MinPartySize"/> or more pay per guest.
/// Applied to online bookings only; phone bookings and walk-ins never take a deposit.
/// </summary>
public sealed record DepositRule(int MinPartySize, long PerGuestCents)
{
    /// <summary>The deposit due for this party, or <c>null</c> when none is due.</summary>
    public Money? DueFor(PartySize partySize, string currency) =>
        partySize.Value >= MinPartySize ? new Money(PerGuestCents * partySize.Value, currency) : null;
}

public enum RefundAmount
{
    Full,
    None,
}

/// <summary>Cancellation rule (BR-07, Design §8.3).</summary>
public sealed record CancellationRule(int FreeUntilHoursBefore, RefundAmount RefundWhenInTime, RefundAmount HostCancelRefund);

/// <summary>A venue's rules (Design §2.2). Booking terms are copied from here at booking time (§2.6).</summary>
public sealed record VenueSettings(
    int SlotMinutes,
    TimeOnly ServiceStart,
    TimeOnly LastBooking,
    IReadOnlyList<TurnTimeRule> TurnTimes,
    int PacingCoversPerSlot,
    int MaxOnlinePartySize,
    DepositRule Deposit,
    CancellationRule Cancellation,
    int NotifiedTimeoutMinutes,
    int PaymentHoldMinutes,
    int LateBillMatchMinutes)
{
    public Result Validate()
    {
        if (SlotMinutes <= 0 || 60 % SlotMinutes != 0)
        {
            return Error.Validation("slotMinutes", "slot_minutes_invalid");
        }

        if (LastBooking < ServiceStart)
        {
            return Error.Validation("lastBooking", "service_hours_invalid");
        }

        if (TurnTimes is null || TurnTimes.Count == 0 || TurnTimes.Any(t => t.Minutes <= 0 || t.MinPartySize > t.MaxPartySize))
        {
            return Error.Validation("turnTimes", "turn_times_invalid");
        }

        if (MaxOnlinePartySize is < PartySize.Min or > PartySize.Max)
        {
            return Error.Validation("maxOnlinePartySize", "party_size_out_of_range");
        }

        if (PacingCoversPerSlot <= 0 || NotifiedTimeoutMinutes <= 0 || PaymentHoldMinutes <= 0 || LateBillMatchMinutes < 0)
        {
            return Error.Validation("settings", "settings_invalid");
        }

        if (Deposit.MinPartySize < PartySize.Min || Deposit.PerGuestCents < 0 || Cancellation.FreeUntilHoursBefore < 0)
        {
            return Error.Validation("settings", "settings_invalid");
        }

        return Result.Success();
    }

    /// <summary>
    /// Turn time for the party (BR-02). Parties larger than every rule (phone or walk-in, 9–20)
    /// use the rule with the largest <see cref="TurnTimeRule.MaxPartySize"/>.
    /// </summary>
    public int TurnMinutesFor(PartySize partySize) =>
        (TurnTimes.FirstOrDefault(t => t.Applies(partySize))
         ?? TurnTimes.MaxBy(t => t.MaxPartySize)!).Minutes;

    /// <summary>
    /// BR-01: the start is on the slot grid (every <see cref="SlotMinutes"/> from
    /// <see cref="ServiceStart"/>) and between service start and last booking, in venue time.
    /// </summary>
    public bool IsBookableSlot(DateTimeOffset start, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTime(start, timeZone);
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        if (time < ServiceStart || time > LastBooking)
        {
            return false;
        }

        var sinceStart = time - ServiceStart;
        return sinceStart.Ticks % TimeSpan.FromMinutes(SlotMinutes).Ticks == 0;
    }
}
