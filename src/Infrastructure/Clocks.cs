namespace Reservation.Infrastructure;

/// <summary>
/// The two clocks (Design §5.6). The <b>business clock</b> is the default <see cref="TimeProvider"/>; DemoClock
/// replaces it in the demo, and the domain reads it (the <c>now</c> passed to aggregates, so event
/// <c>occurredAt</c>). The <b>infrastructure clock</b> is the keyed <see cref="TimeProvider"/>
/// <see cref="Infrastructure"/>, always the system clock: workers use it for polling, retries and their own
/// timestamps, so they keep running while the demo clock stands still.
/// </summary>
public static class Clocks
{
    /// <summary>Service key of the infrastructure clock.</summary>
    public const string Infrastructure = "infrastructure";
}
