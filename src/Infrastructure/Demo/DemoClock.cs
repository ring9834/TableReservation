namespace Reservation.Infrastructure.Demo;

/// <summary>How the business clock runs (Design §17).</summary>
public enum DemoClockMode
{
    /// <summary>The system clock.</summary>
    System,

    /// <summary>Today (venue-local date at start-up) at <see cref="DemoClockOptions.FixedLocalTime"/>, standing still.</summary>
    Fixed,
}

/// <summary>Section <c>Demo:Clock</c>. Development and the CI end-to-end run use <c>Fixed</c> at 18:25.</summary>
public sealed class DemoClockOptions
{
    public const string Section = "Demo:Clock";

    public DemoClockMode Mode { get; set; } = DemoClockMode.System;

    /// <summary>Venue-local time of day in <c>Fixed</c> mode, e.g. <c>18:25</c>.</summary>
    public TimeOnly? FixedLocalTime { get; set; }

    public bool IsValid => Mode != DemoClockMode.Fixed || FixedLocalTime is not null;
}

/// <summary>
/// The demo's business clock (Design §17). In <c>Fixed</c> mode it answers "today at the fixed local time" for good:
/// the date is the venue-local date of <paramref name="baseClock"/> when the clock is created (at start-up), and
/// <see cref="GetUtcNow"/> never moves. Timestamps and timers stay the base clock's: nothing on the business side
/// waits on timers, and workers use the infrastructure clock anyway (§5.6). Presenter controls, offset mode and
/// reset come in M11.
/// </summary>
public sealed class DemoClock : TimeProvider
{
    private readonly TimeProvider _baseClock;

    public DemoClock(TimeProvider baseClock, TimeOnly fixedLocalTime, TimeZoneInfo venueTimeZone)
    {
        ArgumentNullException.ThrowIfNull(baseClock);
        ArgumentNullException.ThrowIfNull(venueTimeZone);
        _baseClock = baseClock;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(baseClock.GetUtcNow(), venueTimeZone).DateTime);
        Now = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(fixedLocalTime, DateTimeKind.Unspecified), venueTimeZone));
    }

    /// <summary>The fixed instant (UTC).</summary>
    public DateTimeOffset Now { get; }

    public override DateTimeOffset GetUtcNow() => Now;

    public override long TimestampFrequency => _baseClock.TimestampFrequency;

    public override long GetTimestamp() => _baseClock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _baseClock.CreateTimer(callback, state, dueTime, period);

    /// <summary>The business clock for the options: the system clock, or a fixed <see cref="DemoClock"/> for Golden Dragon.</summary>
    public static TimeProvider Create(DemoClockOptions options, TimeProvider systemClock)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options is { Mode: DemoClockMode.Fixed, FixedLocalTime: { } time }
            ? new DemoClock(systemClock, time, GoldenDragon.TimeZone())
            : systemClock;
    }
}

/// <summary>Section <c>Demo</c>, start-up behaviour of the demo data (Development only).</summary>
public sealed class DemoDataOptions
{
    public const string Section = "Demo";

    /// <summary>
    /// When the database holds no booking on today's venue-local date (business clock), reset and reseed at start-up,
    /// so a local database seeded on an earlier day shows tonight again. Default false.
    /// </summary>
    public bool ResetIfStale { get; set; }
}
