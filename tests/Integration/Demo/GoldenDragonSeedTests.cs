using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

/// <summary>
/// The seed fits the demo script at 18:25 (Requirements §10, Design §17 seed table).
/// Steps: 1 online booking with deposit, 3 walk-in and waitlist, 4 table frees, 5 reminder confirm/cancel, 6 POS.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GoldenDragonSeedTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bookings_match_the_seed_table()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (_, bookings) = await LoadAsync(database);
        var yesterdayNoon = GoldenDragon.At(DemoTime.Tonight.AddDays(-1), new TimeOnly(12, 0));

        var expected = new (string Ref, int Hour, int Min, int Party, BookingChannel Channel, BookingStatus Status, string Table, bool Deposit)[]
        {
            ("GD2A4K", 17, 30, 4, BookingChannel.Phone, BookingStatus.Seated, "T4", false),
            ("GD3B7M", 17, 30, 5, BookingChannel.Phone, BookingStatus.Seated, "T9", false),
            ("GD4C8N", 17, 45, 3, BookingChannel.Online, BookingStatus.Seated, "W3", false),
            ("GD5D9P", 18, 0, 7, BookingChannel.Phone, BookingStatus.Seated, "T10", false),
            ("GD6E2Q", 18, 15, 4, BookingChannel.Online, BookingStatus.Seated, "W4", false),
            ("GD7F3R", 18, 45, 4, BookingChannel.Online, BookingStatus.Confirmed, "T5", false),
            ("GD8G4S", 19, 0, 6, BookingChannel.Online, BookingStatus.Booked, "T7", true),
            ("GD9H5T", 19, 0, 6, BookingChannel.Phone, BookingStatus.Booked, "T8", false),
            ("GDA2J6", 19, 15, 4, BookingChannel.Online, BookingStatus.Booked, "T6", false),
            ("GDB3K7", 20, 30, 2, BookingChannel.Online, BookingStatus.Booked, "W1", false),
            ("GDC4L8", 20, 30, 6, BookingChannel.Online, BookingStatus.Booked, "T9", true),
        };

        Assert.Equal(expected.Length, bookings.Count);
        foreach (var e in expected)
        {
            var booking = bookings.Single(b => b.Reference.Value == e.Ref);
            var start = DemoTime.At(e.Hour, e.Min);

            Assert.Equal(e.Party, booking.PartySize.Value);
            Assert.Equal(e.Channel, booking.Channel);
            Assert.Equal(e.Status, booking.Status);
            Assert.Equal(start, booking.Period.Start);
            Assert.Equal(start + booking.Terms.Turn, booking.Period.End);
            Assert.Equal(GoldenDragon.TableId(e.Table), Assert.Single(booking.Tables).TableId);
            Assert.True(booking.Tables[0].Active);
            Assert.Equal(yesterdayNoon, booking.CreatedAt);
            Assert.Null(booking.CompletedAt);
            Assert.Equal(e.Status == BookingStatus.Seated ? start : null, booking.SeatedAt);
            Assert.NotNull(booking.GuestId);

            if (e.Deposit)
            {
                Assert.Equal(DepositStatus.Captured, booking.Deposit!.Status);
                Assert.Equal(Money.FromNzd(2000 * e.Party), booking.Deposit.Amount);
                Assert.Null(booking.HoldExpiresAt);
            }
            else
            {
                Assert.Null(booking.Deposit);
            }
        }
    }

    [Fact]
    public async Task Step_1_online_party_of_6_at_20_00_has_a_table_and_pacing_room()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (venue, bookings) = await LoadAsync(database);
        var start = DemoTime.At(20, 0);
        var period = TimeRange.FromStart(start, TimeSpan.FromMinutes(venue.Settings.TurnMinutesFor(PartySize.Of(6))));

        var free = FreeTables(venue, bookings, period, seats: 6);

        Assert.Equal(["T10"], free);
        Assert.True(CoversStartingAt(bookings, start) + 6 <= venue.Settings.PacingCoversPerSlot);
        Assert.True(CoversStartingAt(bookings, DemoTime.At(19, 30)) + 2 <= venue.Settings.PacingCoversPerSlot);
        Assert.Contains("T1", FreeTables(venue, bookings, TimeRange.FromStart(DemoTime.At(19, 30), TimeSpan.FromMinutes(75)), seats: 2));
    }

    [Fact]
    public async Task Step_1_nineteen_hundred_is_full_by_pacing()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (venue, bookings) = await LoadAsync(database);

        Assert.Equal(venue.Settings.PacingCoversPerSlot, CoversStartingAt(bookings, DemoTime.At(19, 0)));
    }

    [Fact]
    public async Task Step_3_a_walk_in_of_2_fits_but_a_walk_in_of_4_does_not()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (venue, bookings) = await LoadAsync(database);

        var forTwo = FreeTables(venue, bookings, new TimeRange(DemoTime.Now, DemoTime.At(19, 40)), seats: 2);
        var forFour = FreeTables(venue, bookings, new TimeRange(DemoTime.Now, DemoTime.At(19, 55)), seats: 4);

        Assert.Contains("T1", forTwo);
        Assert.Empty(forFour);
    }

    [Fact]
    public async Task Step_4_bill_paid_on_T4_frees_it_for_the_waiting_party_of_4()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (venue, bookings) = await LoadAsync(database);
        var seatedAtT4 = bookings.Single(b => b.Status == BookingStatus.Seated && b.Tables.Any(t => t.TableId == GoldenDragon.TableId("T4")));

        var bill = new BookingBill("B-1001", Money.FromNzd(18_500), null, DemoTime.Now, new PosMessageId(Guid.CreateVersion7()));
        Assert.True(seatedAtT4.Complete(CompletionSource.BillClosed, bill, DemoTime.Now).IsSuccess);

        var forFour = FreeTables(venue, bookings, new TimeRange(DemoTime.Now, DemoTime.At(19, 55)), seats: 4);
        Assert.Equal(["T4"], forFour);
    }

    [Fact]
    public async Task Step_5_only_the_two_20_30_bookings_get_reminders_and_the_cancel_is_refunded()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var (_, bookings) = await LoadAsync(database);

        var remindable = bookings.Where(b => ReminderPolicy.CanSend(b, DemoTime.Now)).Select(b => b.Reference.Value).Order();
        Assert.Equal(["GDB3K7", "GDC4L8"], remindable);

        var withDeposit = bookings.Single(b => b.Reference.Value == "GDC4L8");
        var decision = withDeposit.Cancel(Actor.ForGuest(withDeposit.GuestId), CancelReason.GuestRequest, DemoTime.Now);
        Assert.Equal(RefundDecision.Full, decision.Value);
    }

    [Fact]
    public async Task Step_6_the_arriving_party_can_be_seated_at_18_25()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var db = database.Context();
        var arriving = await db.Bookings.SingleAsync(b => b.Status == BookingStatus.Confirmed, Ct);

        Assert.True(arriving.Seat([GoldenDragon.TableId("T5")], DemoTime.Now, Actor.ForStaff("demo-host", "Host")).IsSuccess);
        await db.SaveChangesAsync(Ct); // passes the exclusion constraint
    }

    [Theory]
    [InlineData("2026-11-24T09:00:00+13:00")] // morning
    [InlineData("2026-11-24T23:30:00+13:00")] // after service
    [InlineData("2027-04-04T12:00:00+12:00")] // the day daylight saving ends
    [InlineData("2026-09-27T12:00:00+13:00")] // the day daylight saving starts
    public async Task The_seed_works_on_any_day(string nowText)
    {
        var now = DateTimeOffset.Parse(nowText, System.Globalization.CultureInfo.InvariantCulture);
        await using var database = await TestDatabase.CreateAsync(postgres, seed: false);

        await database.SeedAsync(now);

        await using var db = database.Context();
        var bookings = await db.Bookings.ToListAsync(Ct);
        var tonight = GoldenDragon.ServiceDate(now);
        Assert.Equal(11, bookings.Count);
        Assert.All(bookings, b => Assert.Equal(tonight, GoldenDragon.ServiceDate(b.Period.Start)));
        Assert.Equal(GoldenDragon.At(tonight, new TimeOnly(17, 30)), bookings.Min(b => b.Period.Start));
    }

    private static async Task<(Venue Venue, List<Booking> Bookings)> LoadAsync(TestDatabase database)
    {
        await using var db = database.Context();
        return (await db.Venues.SingleAsync(Ct), await db.Bookings.ToListAsync(Ct));
    }

    /// <summary>Tables with at least <paramref name="seats"/> seats and no active assignment overlapping <paramref name="period"/>.</summary>
    private static List<string> FreeTables(Venue venue, IEnumerable<Booking> bookings, TimeRange period, int seats)
    {
        var busy = bookings.Where(b => b.IsActive)
            .SelectMany(b => b.Tables)
            .Where(a => a.Active && a.Period.Overlaps(period))
            .Select(a => a.TableId)
            .ToHashSet();

        return venue.Tables.Where(t => t.Seats >= seats && !busy.Contains(t.Id)).Select(t => t.Name).Order().ToList();
    }

    private static int CoversStartingAt(IEnumerable<Booking> bookings, DateTimeOffset start) =>
        bookings.Where(b => b.IsActive && b.Period.Start == start).Sum(b => b.PartySize.Value);
}
