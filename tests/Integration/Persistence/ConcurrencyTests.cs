using Microsoft.EntityFrameworkCore;
using Npgsql;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Persistence;

/// <summary>The database rules of Design §12: one party per table (exclusion constraint) and xmin row versions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_bookings_for_the_last_table_exactly_one_succeeds()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var tomorrow = DemoTime.Tonight.AddDays(1);
        var start = GoldenDragon.At(tomorrow, new TimeOnly(19, 0));
        var t10 = GoldenDragon.TableId("T10"); // the only 8-seat table

        async Task<Exception?> BookAsync(string reference)
        {
            await using var db = database.Context();
            var venue = await db.Venues.SingleAsync(Ct);
            var booking = Booking.Create(
                venue, null, BookingReference.Parse(reference).Value, BookingChannel.Phone, PartySize.Of(8),
                start, [t10], null, null, DemoTime.Now).Value;
            db.Bookings.Add(booking);
            try
            {
                await db.SaveChangesAsync(Ct);
                return null;
            }
            catch (DbUpdateException ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(BookAsync("RACEAA"), BookAsync("RACEBB"));

        Assert.Single(results, r => r is null);
        var failure = Assert.Single(results, r => r is not null);
        var postgresError = Assert.IsType<PostgresException>(failure!.InnerException);
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, postgresError.SqlState); // 23P01 → 409 slot_taken
        Assert.Equal("ex_table_assignments_no_overlap", postgresError.ConstraintName);

        await using var check = database.Context();
        Assert.Equal(1, await check.Bookings.CountAsync(b => b.Reference == BookingReference.Parse("RACEAA").Value || b.Reference == BookingReference.Parse("RACEBB").Value, Ct));
    }

    [Fact]
    public async Task A_cancelled_booking_no_longer_holds_its_table()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var db = database.Context();
        var venue = await db.Venues.SingleAsync(Ct);
        var twentyThirty = await db.Bookings.SingleAsync(b => b.Reference == BookingReference.Parse("GDB3K7").Value, Ct);

        twentyThirty.Cancel(Actor.ForGuest(twentyThirty.GuestId), CancelReason.GuestRequest, DemoTime.Now);
        db.Bookings.Add(Booking.Create(
            venue, null, BookingReference.Parse("NEWW2A").Value, BookingChannel.Phone, PartySize.Of(2),
            DemoTime.At(20, 30), [GoldenDragon.TableId("W1")], null, null, DemoTime.Now).Value);

        await db.SaveChangesAsync(Ct); // the inactive assignment is outside the constraint (WHERE active)
    }

    [Fact]
    public async Task A_stale_update_fails_with_a_concurrency_error()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var reference = BookingReference.Parse("GDA2J6").Value; // 19:15, Booked, T6
        await using var first = database.Context();
        await using var second = database.Context();
        var inFirst = await first.Bookings.SingleAsync(b => b.Reference == reference, Ct);
        var inSecond = await second.Bookings.SingleAsync(b => b.Reference == reference, Ct);

        Assert.True(inFirst.Confirm(Actor.ForStaff("demo-host", "Host"), DemoTime.Now).IsSuccess);
        await first.SaveChangesAsync(Ct);

        Assert.True(inSecond.Seat([GoldenDragon.TableId("T6")], DemoTime.Now, Actor.ForStaff("demo-host", "Host")).IsSuccess);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct)); // → 409 version_conflict

        await using var check = database.Context();
        Assert.Equal(BookingStatus.Confirmed, (await check.Bookings.SingleAsync(b => b.Reference == reference, Ct)).Status);
    }
}
