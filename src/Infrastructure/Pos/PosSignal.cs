using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// Wakes the <see cref="PosDeliveryWorker"/> (Design §6.4): rung by <see cref="PosMessageChangeInterceptor"/> after a
/// commit that queued an outbound message, and when the mock POS comes back online. Timeouts run on the infrastructure clock.
/// </summary>
public sealed class PosSignal([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock) : WorkerSignal(clock);
