using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <summary>A new reference, unique within the venue (the venue filter limits the check to it).</summary>
internal static class BookingReferences
{
    private const int MaxAttempts = 5;

    public static async Task<BookingReference> NewAsync(ReservationsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (var i = 0; i < MaxAttempts; i++)
        {
            var reference = BookingReference.Generate(Random.Shared);
            if (!await db.Bookings.AnyAsync(b => b.Reference == reference, cancellationToken))
            {
                return reference;
            }
        }

        throw new InvalidOperationException("Could not generate a unique booking reference.");
    }
}
