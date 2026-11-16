namespace Reservation.Infrastructure.Demo;

/// <summary>Which business clock the installation uses (section <c>Demo:Clock</c>, Design §17).</summary>
public enum DemoClockMode
{
    /// <summary>The system clock.</summary>
    System,

    /// <summary>
    /// The <see cref="DemoClock"/>: after a reset (or on a fresh database) tonight at <see cref="DemoClockOptions.FixedLocalTime"/>,
    /// standing still; the presenter can move it or let it run (M11a, Design §17.2).
    /// </summary>
    Fixed,
}

/// <summary>Section <c>Demo:Clock</c>. Development and the CI end-to-end run use <c>Fixed</c> at 18:25.</summary>
public sealed class DemoClockOptions
{
    public const string Section = "Demo:Clock";

    public DemoClockMode Mode { get; set; } = DemoClockMode.System;

    /// <summary>Venue-local time of day the demo clock starts at and goes back to on reset, e.g. <c>18:25</c>.</summary>
    public TimeOnly? FixedLocalTime { get; set; }

    public bool IsValid => Mode != DemoClockMode.Fixed || FixedLocalTime is not null;
}

/// <summary>How the demo clock moves (M11a, Design §17.2).</summary>
public enum DemoTimeMode
{
    /// <summary>Business time stands still.</summary>
    Fixed,

    /// <summary>Business time moves with real time from where it was set.</summary>
    Running,
}

/// <summary>
/// The demo clock's state (M11a, Design §17.2), stored in <c>demo_clock</c>. In <see cref="DemoTimeMode.Fixed"/> business
/// time is <see cref="BusinessAt"/>; in <see cref="DemoTimeMode.Running"/> it is <see cref="BusinessAt"/> plus the
/// infrastructure time passed since <see cref="AnchoredAt"/>.
/// </summary>
/// <param name="Mode">Fixed or running.</param>
/// <param name="BusinessAt">Business time at <paramref name="AnchoredAt"/> (UTC).</param>
/// <param name="AnchoredAt">Infrastructure-clock time when the state was set (UTC).</param>
public sealed record DemoClockState(DemoTimeMode Mode, DateTimeOffset BusinessAt, DateTimeOffset AnchoredAt)
{
    /// <summary>Business time when the infrastructure clock reads <paramref name="infrastructureNow"/>. Never earlier than <see cref="BusinessAt"/>.</summary>
    public DateTimeOffset NowAt(DateTimeOffset infrastructureNow) =>
        Mode == DemoTimeMode.Fixed || infrastructureNow <= AnchoredAt
            ? BusinessAt
            : BusinessAt + (infrastructureNow - AnchoredAt);
}

/// <summary>
/// The demo's business clock (Design §17, §17.2). Answers from a <see cref="DemoClockState"/> snapshot in memory, so
/// <see cref="GetUtcNow"/> never touches the database; <see cref="DemoClockStore"/> keeps the snapshot in step with
/// the stored state (start-up, every change, and <see cref="DemoClockSync"/> for other instances). Running mode counts
/// on the <b>infrastructure</b> clock; timestamps and timers are the infrastructure clock's too, so nothing on the
/// infrastructure side moves when business time does (rule 14).
/// </summary>
public sealed class DemoClock : TimeProvider
{
    private readonly TimeProvider _infrastructureClock;
    private readonly Lock _lock = new();
    private DemoClockState _state;
    private long _generation;

    /// <param name="infrastructureClock">Always the system clock outside tests (§5.6).</param>
    /// <param name="resetLocalTime">Venue-local time a reset (and a fresh database) starts at.</param>
    /// <param name="venueTimeZone">Golden Dragon's zone.</param>
    public DemoClock(TimeProvider infrastructureClock, TimeOnly resetLocalTime, TimeZoneInfo venueTimeZone)
    {
        ArgumentNullException.ThrowIfNull(infrastructureClock);
        ArgumentNullException.ThrowIfNull(venueTimeZone);
        _infrastructureClock = infrastructureClock;
        ResetLocalTime = resetLocalTime;
        TimeZone = venueTimeZone;
        _state = ResetState();
    }

    public TimeOnly ResetLocalTime { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>The state in memory.</summary>
    public DemoClockState State => Volatile.Read(ref _state);

    public override DateTimeOffset GetUtcNow() => State.NowAt(_infrastructureClock.GetUtcNow());

    public override long TimestampFrequency => _infrastructureClock.TimestampFrequency;

    public override long GetTimestamp() => _infrastructureClock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _infrastructureClock.CreateTimer(callback, state, dueTime, period);

    /// <summary>
    /// The state a reset puts back: Fixed, today (venue-local date on the infrastructure clock) at
    /// <see cref="ResetLocalTime"/>.
    /// </summary>
    public DemoClockState ResetState()
    {
        var infrastructureNow = _infrastructureClock.GetUtcNow();
        return new DemoClockState(DemoTimeMode.Fixed, At(LocalDate(infrastructureNow), ResetLocalTime), infrastructureNow);
    }

    /// <summary>Counts every <see cref="Apply"/>; a load reads it before going to the database (<see cref="ApplyLoaded"/>).</summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>Takes a state just written (a change or a reset); true when it differs from the one in memory.</summary>
    public bool Apply(DemoClockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            _generation++;
            var changed = _state != state;
            Volatile.Write(ref _state, state);
            return changed;
        }
    }

    /// <summary>
    /// Takes a state read from the database, unless this instance applied another one since <paramref name="generation"/>
    /// was read: a load that started before a change committed must not put the old state back. True when it changed.
    /// </summary>
    public bool ApplyLoaded(DemoClockState state, long generation)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (_generation != generation || _state == state)
            {
                return false;
            }

            _generation++;
            Volatile.Write(ref _state, state);
            return true;
        }
    }

    /// <summary>The venue-local date of an instant.</summary>
    public DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>A venue-local date and time as a UTC instant.</summary>
    public DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), TimeZone));

    /// <summary>The demo clock for the options, starting at <see cref="DemoClockOptions.FixedLocalTime"/> (18:25 when unset).</summary>
    public static DemoClock Create(DemoClockOptions options, TimeProvider infrastructureClock)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DemoClock(infrastructureClock, options.FixedLocalTime ?? GoldenDragon.DemoTime, GoldenDragon.TimeZone());
    }

    /// <summary>The business clock for the options: <paramref name="demoClock"/> in Fixed mode, else the system clock.</summary>
    public static TimeProvider BusinessClock(DemoClockOptions options, DemoClock demoClock)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode == DemoClockMode.Fixed ? demoClock : System;
    }

    /// <summary>True when the stored state is from an earlier day than today on the infrastructure clock.</summary>
    public bool IsStale(DemoClockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return LocalDate(state.NowAt(_infrastructureClock.GetUtcNow())) != LocalDate(_infrastructureClock.GetUtcNow());
    }
}

/// <summary>Section <c>Demo</c>, start-up behaviour of the demo data (Development only).</summary>
public sealed class DemoDataOptions
{
    public const string Section = "Demo";

    /// <summary>
    /// <c>Demo:Enabled</c>, the same switch the API's demo session reads. Infrastructure uses it to put
    /// <see cref="Payments.SeedDepositRouter"/> in front of Stripe (Design §7.3). Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When the database holds no booking on today's venue-local date (business clock), reset and reseed at start-up,
    /// so a local database seeded on an earlier day shows tonight again. Default false.
    /// </summary>
    public bool ResetIfStale { get; set; }
}
