using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// The guard rails for the presenter's clock controls (M11a, Design §17.2). Pure: the caller passes the stored state,
/// the infrastructure time and the closing time. Every change re-anchors on the infrastructure clock, so a running
/// clock carries on from the new time. Only a reset moves business time backwards.
/// </summary>
public static class DemoClockPolicy
{
    /// <summary>The longest single jump.</summary>
    public const int MaxAdvanceMinutes = 360;

    public const string MinutesOutOfRange = "demo_clock_minutes_out_of_range";
    public const string PastClosing = "demo_clock_past_closing";
    public const string Backwards = "demo_clock_backwards";

    /// <summary>Moves business time forward by <paramref name="minutes"/> (1–360), keeping the mode.</summary>
    public static Result<DemoClockState> Advance(
        DemoClockState state, DateTimeOffset infrastructureNow, int minutes, DateTimeOffset closesAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (minutes is < 1 or > MaxAdvanceMinutes)
        {
            return Error.Validation("minutes", MinutesOutOfRange);
        }

        var target = state.NowAt(infrastructureNow).AddMinutes(minutes);
        return target > closesAt
            ? Error.Rule(PastClosing)
            : new DemoClockState(state.Mode, target, infrastructureNow);
    }

    /// <summary>
    /// Sets the mode and, optionally, the time (<paramref name="at"/>, already a business instant on the current demo
    /// date). Without a time the clock stays where it is: Freeze stops it, Run starts it from there. A time earlier than
    /// the current minute is refused; one inside the current minute keeps the current time.
    /// </summary>
    public static Result<DemoClockState> Set(
        DemoClockState state, DateTimeOffset infrastructureNow, DemoTimeMode mode, DateTimeOffset? at, DateTimeOffset closesAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        var now = state.NowAt(infrastructureNow);
        var target = now;
        if (at is { } requested)
        {
            var currentMinute = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), now.Offset);
            if (requested < currentMinute)
            {
                return Error.Rule(Backwards);
            }

            target = requested > now ? requested : now;
            if (target > closesAt)
            {
                return Error.Rule(PastClosing);
            }
        }

        return new DemoClockState(mode, target, infrastructureNow);
    }
}
