using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Api.Features.Bookings.Availability;

internal sealed record TableInfo(TableId Id, string Name, int Seats);

/// <summary>An active table assignment: the table is taken for <see cref="Period"/>.</summary>
internal sealed record BusyPeriod(TableId TableId, TimeRange Period);

/// <summary>An active booking (not a walk-in) that starts at <see cref="StartsAt"/>; counts towards pacing (BR-03).</summary>
internal sealed record BookedCovers(DateTimeOffset StartsAt, int PartySize);

/// <summary>Everything §11.1 needs for one service day. <see cref="Tables"/> is in floor order (area, then table).</summary>
internal sealed record AvailabilityInput(
    VenueSettings Settings,
    TimeZoneInfo TimeZone,
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<BusyPeriod> Busy,
    IReadOnlyList<BookedCovers> Covers,
    DateTimeOffset Now);

/// <summary>A bookable slot and the table BR-05 picks for it.</summary>
internal sealed record SlotOption(DateTimeOffset StartsAt, TimeOnly LocalTime, TableInfo Table);

/// <param name="Slots">Every available slot, or the 3 nearest when the requested time is not available.</param>
/// <param name="RequestedTimeAvailable"><c>null</c> when no time was requested.</param>
internal sealed record AvailabilitySearch(IReadOnlyList<SlotOption> Slots, bool? RequestedTimeAvailable);

/// <summary>
/// Availability and table assignment, Design §11.2. Pure: the caller loads the day's data (<see cref="AvailabilityReader"/>).
/// A slot is offered when it is after <c>now</c>, its covers plus the party stay within the pacing limit (BR-03), and
/// some table that fits is free for the whole turn time (BR-02, BR-05: the smallest such table). Availability is
/// advice; the exclusion constraint and the pacing lock decide at write time (§12).
/// </summary>
internal static class AvailabilityCalculator
{
    /// <summary>AC1.3: how many alternatives to offer when the requested time is full.</summary>
    public const int NearestCount = 3;

    /// <summary>BR-01: every <c>SlotMinutes</c> from service start to last booking, in venue time, as UTC instants.</summary>
    public static IReadOnlyList<(DateTimeOffset StartsAt, TimeOnly LocalTime)> SlotsOn(VenueSettings settings, TimeZoneInfo timeZone, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeZone);
        var slots = new List<(DateTimeOffset, TimeOnly)>();
        for (var time = settings.ServiceStart; time <= settings.LastBooking;)
        {
            var local = date.ToDateTime(time, DateTimeKind.Unspecified);
            if (!timeZone.IsInvalidTime(local))
            {
                slots.Add((ToUtc(date, time, timeZone), time));
            }

            var next = time.AddMinutes(settings.SlotMinutes);
            if (next <= time)
            {
                break; // wrapped past midnight
            }

            time = next;
        }

        return slots;
    }

    /// <summary>A venue-local date and time as a UTC instant (an ambiguous time is read as standard time).</summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), timeZone));

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    public static TimeOnly LocalTime(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>The period the reader must load: first slot to last slot plus the longest turn.</summary>
    public static TimeRange ServiceWindow(VenueSettings settings, TimeZoneInfo timeZone, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var start = ToUtc(date, settings.ServiceStart, timeZone);
        var lastStart = ToUtc(date, settings.LastBooking, timeZone);
        var longestTurn = TimeSpan.FromMinutes(settings.TurnTimes.Max(t => t.Minutes));
        return new TimeRange(start, lastStart + longestTurn);
    }

    /// <summary>The slot at <paramref name="startsAt"/> for <paramref name="partySize"/>, or <c>null</c> when it is not available.</summary>
    public static SlotOption? Evaluate(AvailabilityInput input, DateTimeOffset startsAt, PartySize partySize)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (startsAt <= input.Now)
        {
            return null;
        }

        var settings = input.Settings;
        var slotEnd = startsAt.AddMinutes(settings.SlotMinutes);
        var covers = input.Covers.Where(c => c.StartsAt >= startsAt && c.StartsAt < slotEnd).Sum(c => c.PartySize);
        if (covers + partySize.Value > settings.PacingCoversPerSlot)
        {
            return null;
        }

        var period = TimeRange.FromStart(startsAt, TimeSpan.FromMinutes(settings.TurnMinutesFor(partySize)));
        var table = input.Tables
            .Where(t => t.Seats >= partySize.Value)
            .OrderBy(t => t.Seats) // stable: equal sizes keep floor order
            .FirstOrDefault(t => !input.Busy.Any(b => b.TableId == t.Id && b.Period.Overlaps(period)));
        return table is null ? null : new SlotOption(startsAt, LocalTime(startsAt, input.TimeZone), table);
    }

    /// <summary>§11.2 <c>GetAvailability</c>: all available slots, or the 3 nearest to an unavailable requested time (ties: earlier first).</summary>
    public static AvailabilitySearch Search(AvailabilityInput input, DateOnly date, PartySize partySize, TimeOnly? requestedTime)
    {
        ArgumentNullException.ThrowIfNull(input);
        var available = SlotsOn(input.Settings, input.TimeZone, date)
            .Select(slot => Evaluate(input, slot.StartsAt, partySize))
            .OfType<SlotOption>()
            .ToList();
        if (requestedTime is not { } time)
        {
            return new AvailabilitySearch(available, null);
        }

        var requested = ToUtc(date, time, input.TimeZone);
        if (available.Any(s => s.StartsAt == requested))
        {
            return new AvailabilitySearch(available, true);
        }

        var nearest = available
            .OrderBy(s => (s.StartsAt - requested).Duration())
            .ThenBy(s => s.StartsAt)
            .Take(NearestCount)
            .OrderBy(s => s.StartsAt)
            .ToList();
        return new AvailabilitySearch(nearest, false);
    }
}
