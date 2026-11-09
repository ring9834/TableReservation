using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Persistence;

/// <summary>Every mapped value survives a save and a reload in a fresh context.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RoundTripTests(PostgresFixture postgres)
{
    private static readonly Actor _host = Actor.ForStaff("demo-host", "Host");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_booking_with_terms_deposit_assignments_reminder_and_bill_comes_back_equal()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var made = GoldenDragon.At(DemoTime.Tonight, new TimeOnly(12, 0));
        var start = GoldenDragon.At(DemoTime.Tonight.AddDays(2), new TimeOnly(19, 0));
        var t7 = GoldenDragon.TableId("T7");
        BookingId id;

        // Each step in its own context, so inserts, updates and the owned-collection replace are all exercised.
        await using (var db = database.Context())
        {
            var venue = await db.Venues.SingleAsync(Ct);
            var guest = await db.Guests.OrderBy(g => g.Id).FirstAsync(Ct);
            var booking = Booking.Create(
                venue, guest.Id, BookingReference.Parse("RTRP6X").Value, BookingChannel.Online, PartySize.Of(6),
                start, [t7], "High chair please", "Birthday", made).Value;
            id = booking.Id;
            db.Bookings.Add(booking);
            await db.SaveChangesAsync(Ct);
        }

        await Step(database, id, b =>
        {
            Check(b.CaptureDeposit("pi_round_trip", b.Deposit!.Amount, tableStillFree: true, made.AddMinutes(1)));
            Check(b.RecordReminder(start.AddHours(-24), "Free cancellation until 2 hours before."));
        });
        await Step(database, id, b => Check(b.Seat([t7], start.AddMinutes(5), _host)));

        var bill = new BookingBill("B-7001", Money.FromNzd(42_350), Money.FromNzd(1_500), start.AddMinutes(110), new PosMessageId(Guid.CreateVersion7()));
        var expected = await Step(database, id, b => Check(b.Complete(CompletionSource.BillClosed, bill, start.AddMinutes(112))));

        await using var reload = database.Context();
        var actual = await reload.Bookings.SingleAsync(b => b.Id == id, Ct);

        AssertBookingsEqual(expected, actual);
        Assert.Equal(BookingStatus.Completed, actual.Status);
        Assert.Equal(DepositStatus.Captured, actual.Deposit!.Status);
        Assert.Equal(bill, Assert.Single(actual.Bills));
        Assert.Equal(Money.FromNzd(42_350), actual.BillTotal);
        Assert.Equal("Free cancellation until 2 hours before.", actual.Reminder!.PolicyShown);
        Assert.True(actual.LastEventSequence >= 4);
    }

    [Fact]
    public async Task A_cancelled_booking_keeps_its_reason_and_refund_decision()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var reference = BookingReference.Parse("GDC4L8").Value; // 20:30, deposit captured
        BookingId id;
        await using (var db = database.Context())
        {
            id = (await db.Bookings.SingleAsync(b => b.Reference == reference, Ct)).Id;
        }

        var expected = await Step(database, id, b =>
            Assert.Equal(RefundDecision.Full, b.Cancel(Actor.ForGuest(b.GuestId), CancelReason.GuestRequest, DemoTime.Now).Value));

        await using var reload = database.Context();
        var actual = await reload.Bookings.SingleAsync(b => b.Id == id, Ct);
        AssertBookingsEqual(expected, actual);
        Assert.Equal(CancelReason.GuestRequest, actual.CancellationReason);
        Assert.Equal(RefundDecision.Full, actual.CancellationRefund);
        Assert.False(Assert.Single(actual.Tables).Active);
    }

    [Fact]
    public async Task Guest_waitlist_entry_and_message_come_back_equal()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var now = DemoTime.Now;
        Guest guest;
        WaitlistEntry entry;
        Message message;

        await using (var db = database.Context())
        {
            guest = Guest.Create(GoldenDragon.VenueId, PersonName.Create("周杰 Zhou Jie").Value, PhoneNumber.Create("+64 21 011 0099").Value,
                "zhou@example.test", Language.Zh, "Peanuts", now);
            guest.RecordVisit(Money.FromNzd(9_900), now);
            entry = WaitlistEntry.Join(GoldenDragon.VenueId, guest.Id, PartySize.Of(4), 25, now).Value;
            Check(entry.Notify(GoldenDragon.TableId("T4"), now.AddMinutes(20)));
            message = Message.Queue(GoldenDragon.VenueId, guest.Id, MessagePurpose.TableReady, MessageChannel.Sms, Language.Zh,
                "您的餐桌已准备好", [new MessageAction("confirm", "https://example.test/c"), new MessageAction("cancel", "https://example.test/x")],
                entry.Id.Value, now.AddMinutes(20));
            db.AddRange(guest, entry, message);
            await db.SaveChangesAsync(Ct);
        }

        await using var reload = database.Context();
        var g = await reload.Guests.SingleAsync(x => x.Id == guest.Id, Ct);
        var w = await reload.WaitlistEntries.SingleAsync(x => x.Id == entry.Id, Ct);
        var m = await reload.Messages.SingleAsync(x => x.Id == message.Id, Ct);

        Assert.Equal((guest.Name, guest.Mobile, guest.Email, guest.Language, guest.AllergyNotes, guest.VisitCount, guest.LastBillTotal, guest.LastVisitAt, guest.LastEventSequence),
            (g.Name, g.Mobile, g.Email, g.Language, g.AllergyNotes, g.VisitCount, g.LastBillTotal, g.LastVisitAt, g.LastEventSequence));
        Assert.Equal((entry.GuestId, entry.PartySize, entry.QuotedWaitMinutes, entry.Status, entry.JoinedAt, entry.NotifiedAt, entry.NotifiedTableId, entry.LastEventSequence),
            (w.GuestId, w.PartySize, w.QuotedWaitMinutes, w.Status, w.JoinedAt, w.NotifiedAt, w.NotifiedTableId, w.LastEventSequence));
        Assert.Equal((message.Purpose, message.Channel, message.Language, message.Body, message.SubjectId, message.Status, message.ExpiresAt, message.NextAttemptAt),
            (m.Purpose, m.Channel, m.Language, m.Body, m.SubjectId, m.Status, m.ExpiresAt, m.NextAttemptAt));
        Assert.Equal(message.Actions, m.Actions);
        Assert.NotEqual(0u, g.Version);
    }

    /// <summary>Loads the booking in a new context, applies <paramref name="change"/>, saves and returns it.</summary>
    private static async Task<Booking> Step(TestDatabase database, BookingId id, Action<Booking> change)
    {
        await using var db = database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == id, Ct);
        change(booking);
        await db.SaveChangesAsync(Ct);
        return booking;
    }

    private static void Check(Result result) => Assert.True(result.IsSuccess, result.Error?.Code);

    private static void AssertBookingsEqual(Booking expected, Booking actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.VenueId, actual.VenueId);
        Assert.Equal(expected.GuestId, actual.GuestId);
        Assert.Equal(expected.Reference, actual.Reference);
        Assert.Equal(expected.Channel, actual.Channel);
        Assert.Equal(expected.PartySize, actual.PartySize);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.Occasion, actual.Occasion);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.HoldExpiresAt, actual.HoldExpiresAt);
        Assert.Equal(expected.SeatedAt, actual.SeatedAt);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.CompletedBy, actual.CompletedBy);
        Assert.Equal(expected.CancellationReason, actual.CancellationReason);
        Assert.Equal(expected.CancellationRefund, actual.CancellationRefund);
        Assert.Equal(expected.Terms, actual.Terms);
        Assert.Equal(expected.Reminder, actual.Reminder);
        Assert.Equal(
            (expected.Deposit?.Amount, expected.Deposit?.Status, expected.Deposit?.PaymentIntentId, expected.Deposit?.RefundId),
            (actual.Deposit?.Amount, actual.Deposit?.Status, actual.Deposit?.PaymentIntentId, actual.Deposit?.RefundId));
        Assert.Equal(
            expected.Tables.Select(t => (t.TableId, t.Period, t.Active)),
            actual.Tables.Select(t => (t.TableId, t.Period, t.Active)));
        Assert.Equal(expected.Bills, actual.Bills);
        Assert.Equal(expected.LastEventSequence, actual.LastEventSequence);
        Assert.Equal(expected.Version, actual.Version);
        Assert.NotEqual(0u, actual.Version);
    }
}

/// <summary>The venue filter (DM-01) hides other venues' data.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VenueFilterTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_venue_sees_only_its_own_data()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var other = await AddOtherVenueAsync(database);

        await using (var golden = database.Context())
        {
            Assert.Equal(11, await golden.Bookings.CountAsync(Ct));
            Assert.All(await golden.Bookings.ToListAsync(Ct), b => Assert.Equal(GoldenDragon.VenueId, b.VenueId));
            Assert.Equal(11, await golden.Guests.CountAsync(Ct));
            Assert.Equal(GoldenDragon.VenueId, (await golden.Venues.SingleAsync(Ct)).Id);
            Assert.Equal(12, await golden.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).CountAsync(Ct));
        }

        await using (var jade = database.Context(other))
        {
            var venue = await jade.Venues.SingleAsync(Ct);
            Assert.Equal("Jade Garden", venue.Name);
            Assert.Single(venue.Tables);
            Assert.Equal("JADE22", (await jade.Bookings.SingleAsync(Ct)).Reference.Value);
            Assert.Single(await jade.Guests.ToListAsync(Ct));
        }

        await using (var none = database.Context(null))
        {
            Assert.Equal(0, await none.Bookings.CountAsync(Ct));
            Assert.Equal(0, await none.Venues.CountAsync(Ct));
        }
    }

    private static async Task<VenueId> AddOtherVenueAsync(TestDatabase database)
    {
        var now = DemoTime.Now;
        var tableId = new TableId(Guid.CreateVersion7());
        var venue = Domain.Venues.Venue.Create(
            new VenueId(Guid.CreateVersion7()), "Jade Garden", GoldenDragon.TimeZoneId, Money.Nzd, GoldenDragon.Settings(),
            [new Domain.Venues.Area(new AreaId(Guid.CreateVersion7()), "Main", 1,
                [new Domain.Venues.Table(tableId, "J1", 4, new Domain.Venues.FloorPosition(0, 0, 0, Domain.Venues.TableShape.Rect, 90, 90))])]).Value;
        var guest = Guest.Create(venue.Id, PersonName.Create("Other Guest").Value, PhoneNumber.Create("+64 21 011 0001").Value, null, Language.En, null, now);
        var booking = Booking.Create(
            venue, guest.Id, BookingReference.Parse("JADE22").Value, BookingChannel.Phone, PartySize.Of(2),
            DemoTime.At(19, 0), [tableId], null, null, now).Value;

        await using var db = database.Context(venue.Id);
        db.AddRange(venue, guest, booking);
        await db.SaveChangesAsync(Ct);
        return venue.Id;
    }
}
