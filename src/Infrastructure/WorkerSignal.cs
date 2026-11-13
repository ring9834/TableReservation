using System.Threading.Channels;

namespace Reservation.Infrastructure;

/// <summary>
/// An in-process doorbell for one background worker (Design §5.1): <see cref="Notify"/> after a commit wakes the worker
/// at once instead of at its next poll. Several rings before the worker wakes count as one. Timeouts run on the clock
/// passed in, which is always the infrastructure clock.
/// </summary>
public abstract class WorkerSignal(TimeProvider clock)
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
