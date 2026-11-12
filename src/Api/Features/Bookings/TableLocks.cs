using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <summary>
/// §12: overlapping slots take different slot locks, so two requests can pick the same table at once. Inserting both
/// assignments concurrently can deadlock inside the exclusion constraint (<c>40P01</c>); a lock per table, taken in
/// ascending ID order and held until commit, makes the second wait and then fail cleanly with <c>23P01</c> →
/// <c>slot_taken</c>. Booking creation, walk-ins, seating and seating from the waitlist all take these locks, always
/// after the idempotency key and (for creation) the slot lock (Design §12, §14.2).
/// </summary>
internal static class TableLocks
{
    public static async Task LockAsync(ReservationsDbContext db, IEnumerable<TableId> tableIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tableIds);
        foreach (var tableId in tableIds.Select(t => t.Value).Distinct().Order())
        {
            var lockKey = $"table:{tableId}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken);
        }
    }
}
