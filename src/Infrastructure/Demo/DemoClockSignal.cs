using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Rings every job that judges by business time when the demo clock changes (M11a, Design §16, §17.2): the waitlist
/// overdue check and the arriving-soon ticker each <see cref="Subscribe"/> once and wait on "tick or signal", so a
/// clock change is reflected at once rather than on their next tick. In process; another API instance rings its own
/// when <see cref="DemoClockSync"/> picks the change up.
/// </summary>
public sealed class DemoClockSignal([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>A doorbell of the subscriber's own; several rings before it waits count as one.</summary>
    public WorkerSignal Subscribe()
    {
        var subscription = new Subscription(clock);
        lock (_lock)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Notify()
    {
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Notify();
            }
        }
    }

    private sealed class Subscription(TimeProvider clock) : WorkerSignal(clock);
}
