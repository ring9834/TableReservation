namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Lets the demo reset (Design §18.10) run while the background workers are running. Each worker wraps one batch (one
/// transaction) in <see cref="EnterAsync"/>; <see cref="PauseAsync"/> stops new batches from starting, waits until the
/// batches in flight have committed, and holds the workers until the returned pause is disposed. The reset then takes
/// its table locks with nothing of the workers' in the way.
/// <para>
/// In process only: in the demo the workers run inside the API (ADR-005). A reset can still meet an ordinary request's
/// transaction, which its lock timeout and retries cover. Batches never nest, so a worker never waits on itself.
/// </para>
/// </summary>
public sealed class WorkerPause
{
    private readonly Lock _sync = new();
    private int _active;
    private bool _paused;
    private TaskCompletionSource _resumed = Completed();
    private TaskCompletionSource _idle = Completed();

    /// <summary>Enters one worker batch, waiting while a pause is on. Dispose the lease when the batch has committed.</summary>
    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task resumed;
            lock (_sync)
            {
                if (!_paused)
                {
                    if (_active++ == 0)
                    {
                        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return new Lease(this);
                }

                resumed = _resumed.Task;
            }

            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the workers: no new batch starts, and this waits up to <paramref name="timeout"/> for the batches in
    /// flight. <c>null</c> when another pause is already on or the batches don't finish in time (nothing stays paused).
    /// </summary>
    public async Task<Pause?> PauseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task idle;
        lock (_sync)
        {
            if (_paused)
            {
                return null;
            }

            _paused = true;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _active == 0 ? Task.CompletedTask : _idle.Task;
        }

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new Pause(this);
        }
        catch (TimeoutException)
        {
            Resume();
            return null;
        }
        catch
        {
            Resume();
            throw;
        }
    }

    /// <summary>How many batches are running now (tests).</summary>
    public int Active
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    /// <summary>Enters a batch on <paramref name="pause"/>, or does nothing when there is none (workers built by hand in tests).</summary>
    public static ValueTask<Lease> EnterAsync(WorkerPause? pause, CancellationToken cancellationToken) =>
        pause is null ? ValueTask.FromResult(default(Lease)) : pause.EnterAsync(cancellationToken);

    private void Exit()
    {
        lock (_sync)
        {
            if (--_active == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            _resumed.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    /// <summary>One worker batch; disposing it lets a waiting pause go ahead.</summary>
    public readonly struct Lease(WorkerPause? owner) : IDisposable
    {
        public void Dispose() => owner?.Exit();
    }

    /// <summary>The workers held; disposing it lets them go on.</summary>
    public sealed class Pause(WorkerPause owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Resume();
            }
        }
    }
}
