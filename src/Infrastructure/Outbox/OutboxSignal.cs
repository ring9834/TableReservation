using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// The in-process doorbell (Design §5.1): <see cref="Notify"/> after a commit that wrote outbox rows wakes the
/// dispatcher at once instead of at the next poll. Timeouts run on the infrastructure clock.
/// </summary>
public sealed class OutboxSignal([FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>True when signalled, false when <paramref name="timeout"/> passed first.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_channel.Reader.TryRead(out _))
        {
            return true;
        }

        using var timeoutSource = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _channel.Reader.WaitToReadAsync(linked.Token);
            return _channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
