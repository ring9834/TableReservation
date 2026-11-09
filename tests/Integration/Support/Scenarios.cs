using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Tests.Integration.Support;

/// <summary>Domain activity that raises events, on the seeded Golden Dragon at 18:25.</summary>
public static class Scenarios
{
    /// <summary>Tables a walk-in of 2 can take at 18:25 (free until well after the turn).</summary>
    public static readonly string[] FreeTablesForTwo = ["T1", "T2", "T3", "W1", "W2"];

    public const string GuestName = "Lin Meihua";

    public const string GuestMobile = "+64 21 011 0099";

    private static int _nextMobile = 100;

    /// <summary>
    /// A walk-in of 2 seated at <paramref name="table"/>: <c>BookingCreated</c> (1), <c>BookingSeated</c> (2).
    /// Each call makes a new guest with a new fictional mobile unless <paramref name="mobile"/> is given.
    /// </summary>
    public static async Task<Booking> SeatWalkInAsync(ReservationsDbContext db, string table, DateTimeOffset now, string name = GuestName, string? mobile = null)
    {
        mobile ??= $"+64 21 011 0{Interlocked.Increment(ref _nextMobile):000}";
        ArgumentNullException.ThrowIfNull(db);
        var ct = TestContext.Current.CancellationToken;
        var venue = await db.Venues.SingleAsync(ct);
        var guest = Guest.Create(GoldenDragon.VenueId, PersonName.Create(name).Value, PhoneNumber.Create(mobile).Value, null, Language.Zh, null, now);
        var booking = Booking.CreateWalkIn(
            venue, guest.Id, BookingReference.Generate(Random.Shared), PartySize.Of(2), [GoldenDragon.TableId(table)], null, now).Value;
        db.AddRange(guest, booking);
        await db.SaveChangesAsync(ct);
        return booking;
    }

    /// <summary>The host clears the booking's table: <c>BookingCompleted</c>, <c>TableCleared</c>.</summary>
    public static async Task ClearAsync(ReservationsDbContext db, BookingId bookingId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ct = TestContext.Current.CancellationToken;
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId, ct);
        Assert.True(booking.Complete(CompletionSource.ClearedByHost, null, now).IsSuccess);
        await db.SaveChangesAsync(ct);
    }
}
