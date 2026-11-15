using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// Hands out <c>pos_messages.venue_sequence</c> per venue (Design §6.7), for both directions. One statement on the
/// venue's <c>pos_venue_sequences</c> row: <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>. It must run inside the
/// caller's transaction, which then holds the row lock until it commits, so:
/// <list type="bullet">
/// <item>two writers for one venue never get the same number;</item>
/// <item>a rollback (also to a savepoint) gives the number back, so there are no gaps;</item>
/// <item>a venue's rows commit in sequence order, so the delivery worker never sees n + 1 while n is still uncommitted
/// (which <c>max() + 1</c> or a PostgreSQL sequence would allow).</item>
/// </list>
/// The lock is held until the caller commits; in the dispatcher that is the end of its batch (§6.7).
/// </summary>
public sealed class PosSequenceAllocator(ReservationsDbContext db)
{
    public async Task<long> NextAsync(VenueId venueId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("POS sequences are allocated inside the transaction that writes the message.");
        }

        var venue = venueId.Value;
        var allocated = await db.Database.SqlQuery<long>($"""
            INSERT INTO pos_venue_sequences (venue_id, last_sequence) VALUES ({venue}, 1)
            ON CONFLICT (venue_id) DO UPDATE SET last_sequence = pos_venue_sequences.last_sequence + 1
            RETURNING last_sequence AS "Value"
            """).ToListAsync(cancellationToken);
        return allocated.Single();
    }
}
