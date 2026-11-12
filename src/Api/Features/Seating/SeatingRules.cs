using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Api.Features.Seating;

/// <summary>
/// An active table assignment seen from "now": <see cref="Seated"/> when its booking is seated. A seated party holds
/// its table until the host clears it, even after its turn time has passed.
/// </summary>
internal sealed record TableOccupancy(BookingId BookingId, TableId TableId, TimeRange Period, bool Seated);

/// <summary>The floor at business-clock <see cref="Now"/>: tables in floor order and what holds them (Design §11.5).</summary>
internal sealed record FloorNow(
    VenueSettings Settings,
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<TableOccupancy> Occupancy,
    DateTimeOffset Now);

/// <summary>A waiting party, for the suggestion (§11.4).</summary>
internal sealed record WaitingParty(WaitlistEntryId Id, int PartySize, DateTimeOffset JoinedAt);

/// <summary>
/// Walk-in and waitlist rules (Design §11.3–§11.5, BR-05, BR-08, BR-09). Pure: the caller loads <see cref="FloorNow"/>
/// with <see cref="AvailabilityReader.LoadNowAsync"/>. The exclusion constraint still decides at write time (§12).
/// </summary>
internal static class SeatingRules
{
    /// <summary>A quoted wait never exceeds the waitlist's limit (§3.2).</summary>
    public const int MaxQuotedWaitMinutes = Domain.Waitlist.WaitlistEntry.MaxQuotedWaitMinutes;

    /// <summary>The walk-in period: now for the party's turn time (BR-02).</summary>
    public static TimeRange TurnFromNow(FloorNow floor, PartySize partySize)
    {
        ArgumentNullException.ThrowIfNull(floor);
        return TimeRange.FromStart(floor.Now, TimeSpan.FromMinutes(floor.Settings.TurnMinutesFor(partySize)));
    }

    /// <summary>
    /// AC3.1, AC3.2, BR-05: tables that seat the party, have no seated party, and are free for <c>[now, now + turn)</c>;
    /// smallest first, equal sizes in floor order. The first is the suggestion.
    /// </summary>
    public static IReadOnlyList<TableInfo> FreeTablesNow(FloorNow floor, PartySize partySize)
    {
        ArgumentNullException.ThrowIfNull(floor);
        return [.. AvailabilityCalculator.FreeTables(floor.Tables, Busy(floor), TurnFromNow(floor, partySize), partySize)];
    }

    /// <summary>
    /// Whether <paramref name="tableId"/> is free now for the party's turn (seat count not checked). A booking being
    /// seated passes itself as <paramref name="except"/>, so its own assignment doesn't count.
    /// </summary>
    public static bool IsFreeNow(FloorNow floor, TableId tableId, PartySize partySize, BookingId? except = null)
    {
        ArgumentNullException.ThrowIfNull(floor);
        var period = TurnFromNow(floor, partySize);
        return !floor.Occupancy.Any(o => o.TableId == tableId
            && o.BookingId != except
            && (o.Seated || o.Period.Overlaps(period)));
    }

    /// <summary>
    /// BR-08 (§11.3): 0 when a fitting table is free now for the turn; otherwise the minutes until the earliest seated
    /// party on a fitting table is expected to leave (seated time + turn), rounded up to 5 and clamped to 0–240. A party
    /// past its turn counts as leaving now. When no fitting table has a seated party (only upcoming bookings block them),
    /// the earliest end of any assignment on a fitting table is used. <c>null</c> when no table seats the party at all.
    /// </summary>
    public static int? QuotedWait(FloorNow floor, PartySize partySize)
    {
        ArgumentNullException.ThrowIfNull(floor);
        var fitting = floor.Tables.Where(t => t.Seats >= partySize.Value).Select(t => t.Id).ToHashSet();
        if (fitting.Count == 0)
        {
            return null;
        }

        if (FreeTablesNow(floor, partySize).Count > 0)
        {
            return 0;
        }

        var onFitting = floor.Occupancy.Where(o => fitting.Contains(o.TableId)).ToList();
        var seated = onFitting.Where(o => o.Seated).ToList();
        var candidates = seated.Count > 0 ? seated : onFitting;
        if (candidates.Count == 0)
        {
            return 0;
        }

        var earliest = candidates.Min(o => o.Period.End);
        var minutes = Math.Max(0, (earliest - floor.Now).TotalMinutes);
        var rounded = (int)Math.Ceiling(minutes / 5) * 5;
        return Math.Clamp(rounded, 0, MaxQuotedWaitMinutes);
    }

    /// <summary>
    /// BR-09 (§11.4, AC4.2): the first waiting or notified party, by join time, that the table seats and that can sit
    /// there now for its whole turn. The host may pick another. <paramref name="waiting"/> holds active entries only.
    /// </summary>
    public static WaitlistEntryId? SuggestEntry(FloorNow floor, TableInfo table, IEnumerable<WaitingParty> waiting)
    {
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(waiting);
        return waiting
            .OrderBy(w => w.JoinedAt)
            .ThenBy(w => w.Id.Value)
            .Where(w => w.PartySize <= table.Seats && IsFreeNow(floor, table.Id, PartySize.Of(w.PartySize)))
            .Select(w => (WaitlistEntryId?)w.Id)
            .FirstOrDefault();
    }

    /// <summary>A seated party blocks its table until cleared, whatever its period says.</summary>
    private static List<BusyPeriod> Busy(FloorNow floor) =>
        [.. floor.Occupancy.Select(o => new BusyPeriod(o.TableId, o.Seated ? new TimeRange(o.Period.Start, DateTimeOffset.MaxValue) : o.Period))];
}
