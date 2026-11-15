using Microsoft.EntityFrameworkCore;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Empties all venue data and reloads the Golden Dragon seed in one transaction (Design §17). Since M9a it also empties
/// the POS sequence counter and the mock POS's state, which puts the mock back online (§6.7). The endpoint, clock reset
/// and the <c>demoReset</c> broadcast arrive in M11.
/// </summary>
public sealed class DemoReset(ReservationsDbContext db, DemoSeeder seeder)
{
    private const string TruncateAll =
        "TRUNCATE venues, areas, tables, guests, bookings, booking_bills, table_assignments, waitlist_entries, " +
        "messages, outbox, inbox, pos_messages, pos_venue_sequences, audit_log, idempotency_keys, " +
        "mock_pos_state, mock_pos_received, mock_pos_open_tables RESTART IDENTITY CASCADE";

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(TruncateAll, cancellationToken);
        db.ChangeTracker.Clear();
        await seeder.SeedAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
