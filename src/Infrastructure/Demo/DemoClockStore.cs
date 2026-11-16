using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Reads and writes the demo clock's state in <c>demo_clock</c> (M11a, Design §17.2), so it survives a restart and every
/// API instance agrees. A change locks the row (<c>FOR UPDATE</c>) in its own transaction, so two presses at once apply
/// one after the other; after the commit the <see cref="DemoClock"/> in memory takes the new state and the business-time
/// jobs are rung (<see cref="DemoClockSignal"/>).
/// </summary>
public sealed class DemoClockStore(
    ReservationsDbContext db,
    DemoClock clock,
    DemoClockSignal signal,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock)
{
    /// <summary>
    /// Loads the stored state into the clock and returns it (no row: the state in memory). When it differs from the one
    /// in memory the business-time jobs are rung. A change this instance applied while the read was in flight wins.
    /// </summary>
    public async Task<DemoClockState> LoadAsync(CancellationToken cancellationToken)
    {
        var generation = clock.Generation;
        var row = await db.DemoClock.AsNoTracking().SingleOrDefaultAsync(r => r.Id == DemoClockRow.SingletonId, cancellationToken);
        if (row is null)
        {
            // A fresh database: the clock keeps the reset state it started with (nothing has moved it yet).
            return clock.State;
        }

        var state = row.ToState();
        if (clock.ApplyLoaded(state, generation))
        {
            signal.Notify();
        }

        return state;
    }

    /// <summary>
    /// Applies <paramref name="change"/> (given the stored state and the infrastructure time) under the row lock; a
    /// failure changes nothing.
    /// </summary>
    public async Task<Result<DemoClockState>> ChangeAsync(
        Func<DemoClockState, DateTimeOffset, Result<DemoClockState>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = clock.State;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO demo_clock (id, mode, business_at, anchored_at)
            VALUES ({DemoClockRow.SingletonId}, {current.Mode.ToString()}, {current.BusinessAt}, {current.AnchoredAt})
            ON CONFLICT (id) DO NOTHING
            """,
            cancellationToken);
        var row = await db.DemoClock.FromSql($"SELECT * FROM demo_clock WHERE id = {DemoClockRow.SingletonId} FOR UPDATE")
            .AsNoTracking()
            .SingleAsync(cancellationToken);

        var result = change(row.ToState(), infrastructureClock.GetUtcNow());
        if (!result.IsSuccess)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await WriteAsync(db, result.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        clock.Apply(result.Value);
        signal.Notify();
        return result;
    }

    /// <summary>Stores <paramref name="state"/> in the caller's transaction (the reset's, or a change's).</summary>
    internal static Task WriteAsync(ReservationsDbContext db, DemoClockState state, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO demo_clock (id, mode, business_at, anchored_at)
            VALUES ({DemoClockRow.SingletonId}, {state.Mode.ToString()}, {state.BusinessAt}, {state.AnchoredAt})
            ON CONFLICT (id) DO UPDATE SET mode = excluded.mode, business_at = excluded.business_at, anchored_at = excluded.anchored_at
            """,
            cancellationToken);

    /// <summary>
    /// Start-up (M11a): a stored state from an earlier day (a database left from yesterday) goes back to the reset
    /// state, so "tonight" is today again. True when it did.
    /// </summary>
    public async Task<bool> ResetIfStaleAsync(CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        if (!clock.IsStale(state))
        {
            return false;
        }

        var fresh = clock.ResetState();
        await WriteAsync(db, fresh, cancellationToken);
        if (clock.Apply(fresh))
        {
            signal.Notify();
        }

        return true;
    }
}
