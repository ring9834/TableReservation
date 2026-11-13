using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// Wakes the <see cref="MessageDeliveryWorker"/> (Design §7.1, §7.2): rung by <see cref="MessageChangeInterceptor"/>
/// after a commit that queued a message. Timeouts run on the infrastructure clock.
/// </summary>
public sealed class MessageSignal([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock) : WorkerSignal(clock);
