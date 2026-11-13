using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Demo;

[Collection(PostgresCollection.Name)]
public sealed class DemoResetTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reset_leaves_exactly_the_seed_data()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await AddDemoActivityAsync(database);
        await using (var harness = EventHarness.Create(database))
        {
            Assert.True(await harness.DispatchAllAsync() > 0);
        }

        await using (var before = database.Context())
        {
            Assert.True(await before.Outbox.AnyAsync(Ct));
            Assert.True(await before.Inbox.CountAsync(Ct) > 1);
            Assert.True(await before.Audit.AnyAsync(Ct));
            Assert.True(await before.Messages.AnyAsync(Ct));
        }

        await database.ResetAsync(DemoTime.Now);

        await using var db = database.Context();
        var venue = await db.Venues.SingleAsync(Ct);
        var bookings = await db.Bookings.ToListAsync(Ct);
        Assert.Equal(GoldenDragon.VenueId, venue.Id);
        Assert.Equal(GoldenDragon.Tables.Select(t => GoldenDragon.TableId(t.Name).Value).Order(), venue.Tables.Select(t => t.Id.Value).Order());
        Assert.Equal(GoldenDragon.Bookings.Select(b => b.Reference).Order(), bookings.Select(b => b.Reference.Value).Order());
        Assert.Equal(
            GoldenDragon.Bookings.Select(b => (b.Reference, GoldenDragon.TableId(b.Table).Value)).Order(),
            bookings.Select(b => (b.Reference.Value, b.Tables.Single().TableId.Value)).Order());
        Assert.Equal(GoldenDragon.Guests.Count, await db.Guests.CountAsync(Ct));
        Assert.Equal(0, await db.WaitlistEntries.CountAsync(Ct));
        Assert.Equal(0, await db.Messages.CountAsync(Ct));
        Assert.Equal(0, await db.Inbox.CountAsync(Ct));
        Assert.Equal(0, await db.Outbox.CountAsync(Ct));
        Assert.Equal(0, await db.Audit.CountAsync(Ct));
        Assert.Equal(1, await db.Venues.IgnoreQueryFilters().CountAsync(Ct));
    }

    [Fact]
    public async Task Reset_works_on_an_empty_database()
    {
        await using var database = await TestDatabase.CreateAsync(postgres, seed: false);

        await database.ResetAsync(DemoTime.Now);

        await using var db = database.Context();
        Assert.Equal(11, await db.Bookings.CountAsync(Ct));
    }

    private static async Task AddDemoActivityAsync(TestDatabase database)
    {
        var now = DemoTime.Now;
        await using var db = database.Context();
        var venue = await db.Venues.SingleAsync(Ct);
        var guest = Guest.Create(GoldenDragon.VenueId, PersonName.Create("Walk In").Value, PhoneNumber.Create("+64 21 011 0050").Value, null, Language.En, null, now);
        var walkIn = Booking.CreateWalkIn(venue, guest.Id, guest.Name, BookingReference.Parse("WALK22").Value, PartySize.Of(2),
            [GoldenDragon.TableId("T1")], null, now).Value;
        var waiting = WaitlistEntry.Join(GoldenDragon.VenueId, guest.Id, guest.Name, PartySize.Of(4), 20, now).Value;
        var seated = await db.Bookings.SingleAsync(b => b.Reference == BookingReference.Parse("GD2A4K").Value, Ct);
        seated.Complete(CompletionSource.ClearedByHost, null, now);

        var text = Message.Queue(GoldenDragon.VenueId, guest.Id, MessagePurpose.TableReady, MessageChannel.Sms, Language.En,
            "Golden Dragon: your table is ready!", [], waiting.Id.Value, now, DemoTime.InfrastructureStart);

        db.AddRange(guest, walkIn, waiting, text);
        db.Inbox.Add(new InboxEntry("pos-webhook", "msg-1", now));
        await db.SaveChangesAsync(Ct);
    }
}
