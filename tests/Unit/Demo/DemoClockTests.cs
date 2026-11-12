using Reservation.Infrastructure.Demo;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Demo;

/// <summary>The fixed business clock brought forward from M11 (Design §17).</summary>
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
    public void Create_ReturnsTheSystemClock_InSystemMode()
    {
        var system = new TestClock(DateTimeOffset.UnixEpoch);

        var clock = DemoClock.Create(new DemoClockOptions { Mode = DemoClockMode.System, FixedLocalTime = new TimeOnly(18, 25) }, system);

        Assert.Same(system, clock);
    }

    [Fact]
    public void Create_ReturnsAFixedDemoClock_InFixedMode()
    {
        var system = new TestClock(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));

        var clock = DemoClock.Create(new DemoClockOptions { Mode = DemoClockMode.Fixed, FixedLocalTime = new TimeOnly(18, 25) }, system);

        Assert.IsType<DemoClock>(clock);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 5, 25, 0, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public void Options_FixedWithoutATime_AreInvalid()
    {
        Assert.False(new DemoClockOptions { Mode = DemoClockMode.Fixed }.IsValid);
        Assert.True(new DemoClockOptions { Mode = DemoClockMode.System }.IsValid);
    }
}
