using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// Wakes the outbox dispatcher (Design §5.1): rung after a commit that wrote outbox rows, so the dispatcher wakes at
/// once instead of at the next poll. Timeouts run on the infrastructure clock.
/// </summary>
public sealed class OutboxSignal([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock) : WorkerSignal(clock);
