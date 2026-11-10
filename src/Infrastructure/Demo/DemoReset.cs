using Microsoft.EntityFrameworkCore;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Empties all venue data and reloads the Golden Dragon seed in one transaction (Design §17).
/// The endpoint, clock and mock POS reset and the <c>demoReset</c> broadcast arrive in M11.
/// </summary>
public sealed class DemoReset(ReservationsDbContext db, DemoSeeder seeder)
{
    private const string TruncateAll =
        "TRUNCATE venues, areas, tables, guests, bookings, booking_bills, table_assignments, waitlist_entries, " +
        "messages, outbox, inbox, pos_messages, audit_log, idempotency_keys RESTART IDENTITY CASCADE";

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(TruncateAll, cancellationToken);
        db.ChangeTracker.Clear();
        await seeder.SeedAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
