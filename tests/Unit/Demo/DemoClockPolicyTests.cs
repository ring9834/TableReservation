using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;

namespace Reservation.Tests.Unit.Demo;

/// <summary>The presenter's clock guard rails (M11a, Design §17.2).</summary>
public sealed class DemoClockPolicyTests
{
    // 24 Nov 2026, Auckland (UTC+13): 18:25 = 05:25 UTC; closing 22:30 = 09:30 UTC.
    private static readonly DateTimeOffset At1825 = new(2026, 11, 24, 5, 25, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ClosesAt = new(2026, 11, 24, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Infrastructure = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly DemoClockState Fixed = new(DemoTimeMode.Fixed, At1825, Infrastructure);

    [Fact]
    public void Advance_MovesForward_KeepsTheMode_AndReAnchors()
    {
        var later = Infrastructure.AddMinutes(3);

        var result = DemoClockPolicy.Advance(Fixed, later, 11, ClosesAt);

        Assert.Equal(new DemoClockState(DemoTimeMode.Fixed, At1825.AddMinutes(11), later), result.Value);
    }

    [Fact]
    public void Advance_WhileRunning_CountsFromTheRunningTime()
    {
        var running = Fixed with { Mode = DemoTimeMode.Running };
        var later = Infrastructure.AddMinutes(4);

        var result = DemoClockPolicy.Advance(running, later, 5, ClosesAt);

        Assert.Equal(new DemoClockState(DemoTimeMode.Running, At1825.AddMinutes(9), later), result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(361)]
    public void Advance_OutOfRange_IsRefused(int minutes)
    {
        var result = DemoClockPolicy.Advance(Fixed, Infrastructure, minutes, ClosesAt.AddDays(1));

        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal([DemoClockPolicy.MinutesOutOfRange], result.Error.Fields!["minutes"]);
    }

    [Fact]
    public void Advance_ToClosing_IsAllowed_PastIt_IsRefused()
    {
        var toClosing = (int)(ClosesAt - At1825).TotalMinutes; // 245

        Assert.True(DemoClockPolicy.Advance(Fixed, Infrastructure, toClosing, ClosesAt).IsSuccess);
        var result = DemoClockPolicy.Advance(Fixed, Infrastructure, toClosing + 1, ClosesAt);
        Assert.Equal(DemoClockPolicy.PastClosing, result.Error!.Code);
        Assert.Equal(ErrorKind.Rule, result.Error.Kind);
    }

    [Fact]
    public void Set_WithoutATime_KeepsTheCurrentInstant()
    {
        var running = Fixed with { Mode = DemoTimeMode.Running };
        var later = Infrastructure.AddMinutes(6);

        var frozen = DemoClockPolicy.Set(running, later, DemoTimeMode.Fixed, null, ClosesAt);

        Assert.Equal(new DemoClockState(DemoTimeMode.Fixed, At1825.AddMinutes(6), later), frozen.Value);
    }

    [Fact]
    public void Set_ALaterTime_MovesThere()
    {
        var result = DemoClockPolicy.Set(Fixed, Infrastructure, DemoTimeMode.Running, At1825.AddMinutes(35), ClosesAt);

        Assert.Equal(new DemoClockState(DemoTimeMode.Running, At1825.AddMinutes(35), Infrastructure), result.Value);
    }

    [Fact]
    public void Set_AnEarlierTime_IsRefused()
    {
        var result = DemoClockPolicy.Set(Fixed, Infrastructure, DemoTimeMode.Fixed, At1825.AddMinutes(-1), ClosesAt);

        Assert.Equal(DemoClockPolicy.Backwards, result.Error!.Code);
    }

    [Fact]
    public void Set_TheCurrentMinute_WhileRunning_KeepsTheCurrentTime()
    {
        var running = Fixed with { Mode = DemoTimeMode.Running };
        var later = Infrastructure.AddSeconds(30); // business 18:25:30

        var result = DemoClockPolicy.Set(running, later, DemoTimeMode.Fixed, At1825, ClosesAt);

        Assert.Equal(At1825.AddSeconds(30), result.Value.BusinessAt);
    }

    [Fact]
    public void Set_PastClosing_IsRefused()
    {
        var result = DemoClockPolicy.Set(Fixed, Infrastructure, DemoTimeMode.Fixed, ClosesAt.AddMinutes(1), ClosesAt);

        Assert.Equal(DemoClockPolicy.PastClosing, result.Error!.Code);
    }
}
