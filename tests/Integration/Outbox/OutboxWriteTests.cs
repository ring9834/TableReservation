using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Contracts;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Outbox;

/// <summary>Design §5.2: events are written with the change, in the same transaction, by the interceptor.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxWriteTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BookingReference Arriving => BookingReference.Parse(GoldenDragon.Bookings[5].Reference).Value; // #6, Confirmed, T5

    [Fact]
    public async Task Event_is_saved_with_the_change_in_its_envelope()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);

        Booking booking;
        Guid correlationId;
        await using (var scope = harness.Scope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.Set(Guid.CreateVersion7(), null, RequestContext.DemoHost);
            correlationId = context.CorrelationId;
            booking = await Scenarios.SeatWalkInAsync(scope.ServiceProvider.GetRequiredService<ReservationsDbContext>(), "T1", harness.BusinessClock.Now, mobile: Scenarios.GuestMobile);
            Assert.Empty(booking.DomainEvents);
        }

        await using var db = database.Context();
        var rows = await db.Outbox.OrderBy(x => x.Sequence).ToListAsync(Ct);
        Assert.Equal(["BookingCreated", "BookingSeated"], rows.Select(r => r.EventType));
        Assert.Equal([1L, 2L], rows.Select(r => r.Sequence));
        Assert.All(rows, r =>
        {
            Assert.Equal(booking.Id.Value, r.AggregateId);
            Assert.Equal("Booking", r.AggregateType);
            Assert.Equal(1, r.EventVersion);
            Assert.Equal(correlationId, r.CorrelationId);
            Assert.Equal(OutboxStatus.Pending, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Equal(harness.InfrastructureClock.Now, r.CreatedAt);  // infrastructure clock
            Assert.Equal(r.CreatedAt, r.NextAttemptAt);
        });

        var envelope = EventEnvelopeSerializer.ReadEnvelope(rows[1]);
        Assert.Equal(rows[1].EventId, envelope.EventId);
        Assert.Equal(harness.BusinessClock.Now, envelope.OccurredAt);  // business clock, via the domain
        Assert.Equal(GoldenDragon.VenueId.Value, envelope.VenueId);
        Assert.Equal(2, envelope.Sequence);
        Assert.Equal(new EnvelopeActor("staff", "demo-host", "Host"), envelope.Actor);
        Assert.Equal("walkIn", envelope.Payload.GetProperty("source").GetString());
        Assert.Equal(GoldenDragon.TableId("T1").Value, envelope.Payload.GetProperty("tableIds")[0].GetGuid());
        Assert.False(envelope.Payload.TryGetProperty("eventId", out _));

        var json = rows.Select(r => r.Envelope.RootElement.GetRawText()).Aggregate(string.Concat);
        Assert.DoesNotContain(Scenarios.GuestName, json, StringComparison.Ordinal);
        Assert.DoesNotContain("210110099", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0110099", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rolled_back_transaction_leaves_neither_the_change_nor_an_outbox_row()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        long sequenceBefore;

        await using (var db = database.Context())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var booking = await db.Bookings.SingleAsync(b => b.Reference == Arriving, Ct);
            sequenceBefore = booking.LastEventSequence;
            Assert.True(booking.Seat([GoldenDragon.TableId("T5")], DemoTime.Now, RequestContext.DemoHost).IsSuccess);
            await db.SaveChangesAsync(Ct);
            Assert.Equal(1, await db.Outbox.CountAsync(Ct));  // visible inside the transaction
            await transaction.RollbackAsync(Ct);
        }

        await using var check = database.Context();
        Assert.Equal(0, await check.Outbox.CountAsync(Ct));
        var reloaded = await check.Bookings.SingleAsync(b => b.Reference == Arriving, Ct);
        Assert.Equal(BookingStatus.Confirmed, reloaded.Status);
        Assert.Equal(sequenceBefore, reloaded.LastEventSequence);
        Assert.False(await database.Signal.WaitAsync(TimeSpan.Zero, Ct));  // nothing committed, no wake-up
    }

    [Fact]
    public async Task Failed_save_leaves_no_outbox_row_and_a_retried_save_adds_none_twice()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var db = database.Context();

        // T4 is occupied by seated booking #1: the exclusion constraint refuses the walk-in (23P01).
        await Assert.ThrowsAsync<DbUpdateException>(() => Scenarios.SeatWalkInAsync(db, "T4", DemoTime.Now));

        Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());
        await using (var check = database.Context())
        {
            Assert.Equal(0, await check.Outbox.CountAsync(Ct));
        }

        // Undo the bad booking in this context and save something valid: only its own events are written.
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }

        await Scenarios.SeatWalkInAsync(db, "T1", DemoTime.Now);
        await using var after = database.Context();
        Assert.Equal(2, await after.Outbox.CountAsync(Ct));
    }

    [Fact]
    public async Task Sequences_continue_from_LastEventSequence_across_saves()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        long seeded;
        BookingId id;

        await using (var db = database.Context())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Reference == Arriving, Ct);
            seeded = booking.LastEventSequence;
            id = booking.Id;
            Assert.True(seeded > 0);
            Assert.True(booking.Seat([GoldenDragon.TableId("T5")], DemoTime.Now, RequestContext.DemoHost).IsSuccess);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = database.Context())
        {
            await Scenarios.ClearAsync(db, id, DemoTime.At(19, 30));
        }

        await using var check = database.Context();
        var rows = await check.Outbox.Where(x => x.AggregateId == id.Value).OrderBy(x => x.Sequence).ToListAsync(Ct);
        Assert.Equal(
            [("BookingSeated", seeded + 1), ("BookingCompleted", seeded + 2), ("TableCleared", seeded + 3)],
            rows.Select(r => (r.EventType, r.Sequence)));
        var booking2 = await check.Bookings.SingleAsync(b => b.Id == id, Ct);
        Assert.Equal(seeded + 3, booking2.LastEventSequence);
    }

    [Fact]
    public async Task Commit_rings_the_signal_only_after_the_transaction_commits()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var db = database.Context();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        await Scenarios.SeatWalkInAsync(db, "T1", DemoTime.Now);
        Assert.False(await database.Signal.WaitAsync(TimeSpan.Zero, Ct));

        await transaction.CommitAsync(Ct);
        Assert.True(await database.Signal.WaitAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task Seed_writes_no_outbox_rows()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);

        await using var db = database.Context();
        Assert.Equal(11, await db.Bookings.CountAsync(Ct));
        Assert.Equal(0, await db.Outbox.CountAsync(Ct));
    }

    [Fact]
    public async Task Logs_contain_no_personal_data()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);

        await using (var scope = harness.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var booking = await Scenarios.SeatWalkInAsync(db, "T2", harness.BusinessClock.Now, mobile: Scenarios.GuestMobile);
            await Scenarios.ClearAsync(db, booking.Id, harness.BusinessClock.Now.AddHours(1));
        }

        // Deliver everything too, so the dispatcher and the handlers log as well.
        Assert.Equal(4, await harness.DispatchAllAsync());

        var lines = harness.Logs.Lines;
        Assert.Contains(lines, l => l.Category.EndsWith(nameof(OutboxSaveChangesInterceptor), StringComparison.Ordinal));
        var text = string.Join('\n', lines.Select(l => l.Text));
        Assert.DoesNotContain(Scenarios.GuestName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Meihua", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+64210110099", text, StringComparison.Ordinal);
        Assert.DoesNotContain("210110099", text, StringComparison.Ordinal);
        Assert.DoesNotContain("011 0099", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0110099", text, StringComparison.Ordinal);
    }
}
