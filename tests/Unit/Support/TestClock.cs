namespace Reservation.Tests.Unit.Support;

/// <summary>A settable <see cref="TimeProvider"/> for tests (the demo uses <c>DemoClock</c>, Design §17).</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTimeOffset Now => _now;

    public void Set(DateTimeOffset now) => _now = now;

    public DateTimeOffset Advance(TimeSpan by) => _now += by;
}
