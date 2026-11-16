using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Empties all venue data and reloads the Golden Dragon seed in one transaction (Design §17). Since M9a it also empties
/// the POS sequence counter and the mock POS's state, which puts the mock back online (§6.7). Since M10b
/// <c>POST /demo/reset</c> runs it and broadcasts <c>demoReset</c> (Design §18.10). Since M11a it also puts the demo
/// clock back to Fixed at the reset time tonight (<see cref="DemoClock.ResetState"/>), in the same transaction, and the
/// seed is loaded for that evening; after the commit the clock in memory follows and the business-time jobs are rung.
/// <para>
/// With a <c>lockTimeout</c> (the endpoint, while workers run) every table is first locked in one fixed order, the
/// queues the workers claim from first, so a worker transaction that starts afterwards waits at its first statement
/// while holding nothing; a lock that isn't granted in time fails with <c>55P03</c> and the caller retries
/// (<see cref="IsRetryable"/>). Without one (start-up, tests) it waits as long as it takes.
/// </para>
/// </summary>
public sealed class DemoReset(ReservationsDbContext db, DemoSeeder seeder, DemoClock clock, DemoClockSignal clockSignal, TimeProvider businessClock)
{
    /// <summary>Every venue table, in the order they are locked: the worker queues first, the aggregates after.</summary>
    private static readonly string[] _tables =
    [
        "outbox", "messages", "pos_messages", "inbox", "idempotency_keys",
        "mock_pos_state", "mock_pos_received", "mock_pos_open_tables", "pos_venue_sequences", "demo_clock",
        "audit_log", "booking_bills", "table_assignments", "bookings", "waitlist_entries", "guests",
        "tables", "areas", "venues",
    ];

    private static readonly string TableList = string.Join(", ", _tables);

    public async Task ResetAsync(CancellationToken cancellationToken, TimeSpan? lockTimeout = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (lockTimeout is { } timeout)
        {
            var milliseconds = (long)timeout.TotalMilliseconds;
#pragma warning disable EF1002 // a fixed table list and an integer, no input
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = {milliseconds}", cancellationToken);
            await db.Database.ExecuteSqlRawAsync($"LOCK TABLE {TableList} IN ACCESS EXCLUSIVE MODE", cancellationToken);
#pragma warning restore EF1002
        }

#pragma warning disable EF1002 // a fixed table list, no input
        await db.Database.ExecuteSqlRawAsync($"TRUNCATE {TableList} RESTART IDENTITY CASCADE", cancellationToken);
#pragma warning restore EF1002
        db.ChangeTracker.Clear();
        var clockState = clock.ResetState();
        await DemoClockStore.WriteAsync(db, clockState, cancellationToken);
        // With the demo clock as the business clock the seed is for the evening the reset starts on; otherwise (the
        // system clock, or a test's clock) for today on the business clock, as before.
        DateOnly? tonight = ReferenceEquals(businessClock, clock) ? clock.LocalDate(clockState.BusinessAt) : null;
        await seeder.SeedAsync(db, cancellationToken, tonight);
        await transaction.CommitAsync(cancellationToken);
        if (clock.Apply(clockState))
        {
            clockSignal.Notify();
        }
    }

    /// <summary>
    /// A deadlock (<c>40P01</c>), a serialization failure (<c>40001</c>) or a lock not granted in time (<c>55P03</c>):
    /// PostgreSQL rolled the reset back and nothing changed, so running it again is safe.
    /// </summary>
    public static bool IsRetryable(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable };
}
