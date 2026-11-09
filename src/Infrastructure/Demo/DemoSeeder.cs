using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Loads Golden Dragon: 2 areas, 14 tables, 11 guests and 11 bookings for tonight (Design §17).
/// The injected <see cref="TimeProvider"/> only decides which evening is "tonight". The domain is then driven
/// by a seed clock stepped through a plausible timeline (bookings made yesterday at 12:00, deposits paid a minute
/// later, seated parties seated at their start), so stored times look like a real evening whenever the seed runs.
/// </summary>
public sealed class DemoSeeder(TimeProvider timeProvider)
{
    private static readonly TimeOnly _bookedAt = new(12, 0);
    private static readonly TimeOnly _confirmedAt = new(10, 0);

    public async Task SeedAsync(ReservationsDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var tonight = GoldenDragon.ServiceDate(timeProvider.GetUtcNow());
        var clock = new SeedClock(GoldenDragon.At(tonight.AddDays(-1), _bookedAt));

        var venue = CreateVenue();
        var guests = GoldenDragon.Guests.Select(spec => CreateGuest(spec, clock.GetUtcNow())).ToList();
        var bookings = GoldenDragon.Bookings
            .Select(spec => CreateBooking(venue, guests[spec.Number - 1].Id, spec, tonight, clock.GetUtcNow()))
            .ToList();

        // Deposits are paid a minute after booking.
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        foreach (var booking in bookings.Where(b => b.Deposit is not null))
        {
            Check(booking.CaptureDeposit($"pi_seed_{booking.Reference}", booking.Deposit!.Amount, tableStillFree: true, clock.GetUtcNow()));
        }

        // Confirmed bookings were confirmed this morning.
        clock.Set(GoldenDragon.At(tonight, _confirmedAt));
        foreach (var (booking, _) in bookings.Zip(GoldenDragon.Bookings).Where(p => p.Second.Status == GoldenDragon.SeedStatus.Confirmed))
        {
            Check(booking.Confirm(Actor.ForGuest(booking.GuestId), clock.GetUtcNow()));
        }

        // Seated parties were seated at their start time, in order.
        var host = Actor.ForStaff("demo-host", "Host");
        foreach (var (booking, spec) in bookings.Zip(GoldenDragon.Bookings)
                     .Where(p => p.Second.Status == GoldenDragon.SeedStatus.Seated)
                     .OrderBy(p => p.Second.Start))
        {
            clock.Set(booking.Period.Start);
            Check(booking.Seat([GoldenDragon.TableId(spec.Table)], clock.GetUtcNow(), host));
        }

        // Seed data is the starting state, not something that happened: no outbox events.
        venue.ClearDomainEvents();
        guests.ForEach(g => g.ClearDomainEvents());
        bookings.ForEach(b => b.ClearDomainEvents());

        db.Venues.Add(venue);
        db.Guests.AddRange(guests);
        db.Bookings.AddRange(bookings);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Venue CreateVenue()
    {
        var areas = new[] { (GoldenDragon.MainRoomId, "Main room", 1), (GoldenDragon.WindowId, "Window", 2) }
            .Select(a => new Area(
                a.Item1,
                a.Item2,
                a.Item3,
                GoldenDragon.Tables.Where(t => t.Area == a.Item1)
                    .Select(t => new Table(GoldenDragon.TableId(t.Name), t.Name, t.Seats, t.Position))));

        return Venue.Create(GoldenDragon.VenueId, GoldenDragon.Name, GoldenDragon.TimeZoneId, Money.Nzd, GoldenDragon.Settings(), areas).Value;
    }

    private static Guest CreateGuest(GoldenDragon.GuestSpec spec, DateTimeOffset now) =>
        Guest.Create(
            GoldenDragon.VenueId,
            PersonName.Create(spec.Name).Value,
            PhoneNumber.Create(spec.Mobile).Value,
            email: null,
            spec.Language,
            allergyNotes: null,
            now);

    private static Booking CreateBooking(Venue venue, GuestId guestId, GoldenDragon.BookingSpec spec, DateOnly tonight, DateTimeOffset now) =>
        Booking.Create(
            venue,
            guestId,
            BookingReference.Parse(spec.Reference).Value,
            spec.Channel,
            PartySize.Of(spec.PartySize),
            GoldenDragon.At(tonight, spec.Start),
            [GoldenDragon.TableId(spec.Table)],
            notes: null,
            spec.Occasion,
            now).Value;

    private static void Check(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed step failed: {result.Error!.Code}.");
        }
    }

    /// <summary>The seed's own clock, moved forward step by step; whole seconds, UTC.</summary>
    private sealed class SeedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset now) => _now = now.ToUniversalTime();
    }
}
