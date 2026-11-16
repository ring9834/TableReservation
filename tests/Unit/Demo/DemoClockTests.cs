using Reservation.Infrastructure.Demo;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Demo;

/// <summary>The demo business clock: Fixed (brought forward in M5b) and Running (M11a), Design §17, §17.2.</summary>
public sealed class DemoClockTests
{
    private static readonly TimeZoneInfo Auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    [Fact]
    public void Fixed_IsTodayInAucklandAtTheFixedTime()
    {
        // 24 Nov 2026 09:00 Auckland (NZDT, UTC+13) = 23 Nov 20:00 UTC.
        var system = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));

        var clock = new DemoClock(system, new TimeOnly(18, 25), Auckland);

        Assert.Equal(new DateTimeOffset(2026, 11, 24, 5, 25, 0, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public void Fixed_UsesTheAucklandDate_WhenUtcIsStillOnThePreviousDay()
    {
        // 25 Nov 2026 00:30 Auckland = 24 Nov 11:30 UTC: "today" is the 25th, not the UTC date.
        var system = new TestClock(new DateTimeOffset(2026, 11, 24, 11, 30, 0, TimeSpan.Zero));

        var clock = new DemoClock(system, new TimeOnly(18, 25), Auckland);

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 5, 25, 0, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public void Fixed_NeverMoves_WhenTheSystemClockDoes()
    {
        var system = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var clock = new DemoClock(system, new TimeOnly(18, 25), Auckland);
        var first = clock.GetUtcNow();

        system.Advance(TimeSpan.FromDays(1.5));

        Assert.Equal(first, clock.GetUtcNow());
    }

    [Fact]
    public void BusinessClock_IsTheSystemClock_InSystemMode()
    {
        var options = new DemoClockOptions { Mode = DemoClockMode.System };
        var demo = DemoClock.Create(options, new TestClock(DateTimeOffset.UnixEpoch));

        Assert.Same(TimeProvider.System, DemoClock.BusinessClock(options, demo));
    }

    [Fact]
    public void BusinessClock_IsTheDemoClock_InFixedMode()
    {
        var system = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var options = new DemoClockOptions { Mode = DemoClockMode.Fixed, FixedLocalTime = new TimeOnly(18, 25) };

        var demo = DemoClock.Create(options, system);

        Assert.Same(demo, DemoClock.BusinessClock(options, demo));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 5, 25, 0, TimeSpan.Zero), demo.GetUtcNow());
    }

    [Fact]
    public void Running_MovesWithTheInfrastructureClock_FromWhereItWasSet()
    {
        var infrastructure = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var clock = new DemoClock(infrastructure, new TimeOnly(18, 25), Auckland);
        var at1825 = clock.GetUtcNow();

        Assert.True(clock.Apply(new DemoClockState(DemoTimeMode.Running, at1825, infrastructure.GetUtcNow())));
        infrastructure.Advance(TimeSpan.FromMinutes(7));

        Assert.Equal(at1825.AddMinutes(7), clock.GetUtcNow());
    }

    [Fact]
    public void Apply_ReportsWhetherTheStateChanged()
    {
        var infrastructure = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var clock = new DemoClock(infrastructure, new TimeOnly(18, 25), Auckland);

        Assert.False(clock.Apply(clock.State with { }));
        Assert.True(clock.Apply(clock.State with { Mode = DemoTimeMode.Running }));
    }

    [Fact]
    public void ALoad_ThatStartedBeforeAChange_DoesNotPutTheOldStateBack()
    {
        var infrastructure = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var clock = new DemoClock(infrastructure, new TimeOnly(18, 25), Auckland);
        var old = clock.State;
        var generation = clock.Generation; // the sync starts reading the row

        var advanced = old with { BusinessAt = old.BusinessAt.AddMinutes(15) };
        Assert.True(clock.Apply(advanced)); // a change commits meanwhile

        Assert.False(clock.ApplyLoaded(old with { Mode = DemoTimeMode.Running }, generation));
        Assert.Equal(advanced, clock.State);
        Assert.True(clock.ApplyLoaded(old, clock.Generation)); // the next load applies
    }

    [Fact]
    public void IsStale_WhenTheStoredStateIsFromAnEarlierDay()
    {
        var infrastructure = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
        var clock = new DemoClock(infrastructure, new TimeOnly(18, 25), Auckland);
        var today = clock.State;

        Assert.False(clock.IsStale(today));
        infrastructure.Advance(TimeSpan.FromDays(1));
        Assert.True(clock.IsStale(today));
    }

    [Fact]
    public void Options_FixedWithoutATime_AreInvalid()
    {
        Assert.False(new DemoClockOptions { Mode = DemoClockMode.Fixed }.IsValid);
        Assert.True(new DemoClockOptions { Mode = DemoClockMode.System }.IsValid);
    }
}
