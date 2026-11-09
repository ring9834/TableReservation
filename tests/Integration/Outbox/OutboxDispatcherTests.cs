using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Reservation.Api.Health;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Outbox;

/// <summary>Design §5.3–§5.4: delivery, inbox, retries, per-aggregate order and concurrent workers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxDispatcherTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_event_reaches_every_handler_once_in_sequence_order()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var booking = await SeatWalkInAsync(harness, "T1");

        Assert.Equal(2, await harness.DispatchAllAsync());

        var rows = await RowsAsync(database);
        Assert.All(rows, r =>
        {
            Assert.Equal(OutboxStatus.Dispatched, r.Status);
            Assert.Equal(harness.InfrastructureClock.Now, r.DispatchedAt);
            Assert.Null(r.LastError);
        });
        Assert.Equal([1L, 2L], harness.Recorder.For(RecordingHandler<BookingCreated>.Consumer).Select(d => d.Sequence));
        Assert.Equal([1L, 2L], harness.Recorder.For(ProbeHandler<BookingCreated>.Consumer).Select(d => d.Sequence));

        await using var db = database.Context();
        var audit = await db.Audit.OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Equal(rows.Select(r => r.EventId), audit.Select(a => a.EventId));
        Assert.Equal(["BookingCreated", "BookingSeated"], audit.Select(a => a.EventType));
        Assert.All(audit, a => Assert.Equal(booking.Id.Value, a.AggregateId));
        Assert.Equal("system", audit[0].Actor.RootElement.GetProperty("type").GetString());
        Assert.Equal(harness.BusinessClock.Now, audit[0].OccurredAt);
        Assert.Equal(2, audit[0].Summary.RootElement.GetProperty("partySize").GetInt32());

        // One inbox row per wrapped consumer and event; the unwrapped probe has none.
        var inbox = await db.Inbox.ToListAsync(Ct);
        Assert.Equal(6, inbox.Count);
        Assert.Equal([AuditHandler<BookingCreated>.Consumer, FailingHandler<BookingCreated>.Consumer, RecordingHandler<BookingCreated>.Consumer],
            inbox.Select(i => i.Consumer).Distinct().Order());
        Assert.All(inbox, i => Assert.Equal(harness.InfrastructureClock.Now, i.ProcessedAt));
    }

    [Fact]
    public async Task Delivering_the_same_event_twice_has_one_effect_per_handler()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        await SeatWalkInAsync(harness, "T1");
        await harness.DispatchAllAsync();

        // As if the worker crashed after the handlers ran but before the rows were marked.
        await using (var db = database.Context())
        {
            await db.Outbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OutboxStatus.Pending).SetProperty(x => x.DispatchedAt, (DateTimeOffset?)null), Ct);
        }

        Assert.Equal(2, await harness.DispatchAllAsync());

        Assert.Equal(4, harness.Recorder.For(ProbeHandler<BookingCreated>.Consumer).Count);      // delivered twice
        Assert.Equal(2, harness.Recorder.For(RecordingHandler<BookingCreated>.Consumer).Count);  // handled once
        Assert.Equal(2, harness.Recorder.For(FailingHandler<BookingCreated>.Consumer).Count);
        await using var check = database.Context();
        Assert.Equal(2, await check.Audit.CountAsync(Ct));
        Assert.Equal(6, await check.Inbox.CountAsync(Ct));
        Assert.All(await RowsAsync(database), r => Assert.Equal(OutboxStatus.Dispatched, r.Status));
    }

    [Fact]
    public async Task Failing_handler_is_retried_by_the_backoff_then_failed_holding_only_its_aggregate()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var clock = harness.InfrastructureClock;
        var businessNow = harness.BusinessClock.Now;
        var failing = await SeatWalkInAsync(harness, "T1");
        var healthy = await SeatWalkInAsync(harness, "T2");
        harness.Recorder.Fail(failing.Id.Value);

        await harness.DispatchAllAsync();

        var first = await RowAsync(database, failing.Id, 1);
        Assert.Equal(OutboxStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(5), first.NextAttemptAt);
        Assert.StartsWith("failing: InvalidOperationException: Simulated handler failure.", first.LastError, StringComparison.Ordinal);
        Assert.Equal(OutboxStatus.Pending, (await RowAsync(database, failing.Id, 2)).Status);  // held behind sequence 1
        Assert.All(await RowsAsync(database, healthy.Id), r => Assert.Equal(OutboxStatus.Dispatched, r.Status));

        // The other handlers' work stayed; the failing handler's own inbox row and side effect were rolled back.
        await using (var db = database.Context())
        {
            Assert.True(await db.Audit.AnyAsync(a => a.EventId == first.EventId, Ct));
            var inbox = await db.Inbox.Where(i => i.MessageId == first.EventId.ToString()).Select(i => i.Consumer).OrderBy(c => c).ToListAsync(Ct);
            Assert.Equal([AuditHandler<BookingCreated>.Consumer, RecordingHandler<BookingCreated>.Consumer], inbox);
        }

        Assert.Single(harness.Recorder.For(RecordingHandler<BookingCreated>.Consumer), d => d.EventId == first.EventId);

        // Not due yet: nothing happens a second early.
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, await harness.DispatchAllAsync());

        // Walk the schedule on the infrastructure clock until the row gives up.
        var delays = new List<TimeSpan>();
        var row = first;
        while (row.Status == OutboxStatus.Pending)
        {
            clock.Set(row.NextAttemptAt);
            Assert.Equal(1, await harness.Dispatcher.DispatchBatchAsync(Ct));
            var next = await RowAsync(database, failing.Id, 1);
            if (next.Status == OutboxStatus.Pending)
            {
                delays.Add(next.NextAttemptAt - clock.Now);
            }

            row = next;
        }

        Assert.Equal(OutboxStatus.Failed, row.Status);
        Assert.Equal(52, row.Attempts);
        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)],
            delays.Take(3));
        Assert.All(delays.Skip(3), d => Assert.Equal(TimeSpan.FromMinutes(30), d));
        Assert.True(clock.Now - row.CreatedAt <= TimeSpan.FromHours(24));
        Assert.Contains(harness.Logs.Lines, l => l.Level == LogLevel.Error && l.Text.Contains("marked failed", StringComparison.Ordinal));

        // The handlers that succeeded were not run again on any retry.
        Assert.Single(harness.Recorder.For(RecordingHandler<BookingCreated>.Consumer), d => d.EventId == first.EventId);
        await using (var db = database.Context())
        {
            Assert.Equal(1, await db.Audit.CountAsync(a => a.EventId == first.EventId, Ct));
        }

        // Later events of the failed aggregate stay pending; other aggregates still flow.
        var other = await SeatWalkInAsync(harness, "T3");
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, await harness.DispatchAllAsync());
        Assert.All(await RowsAsync(database, other.Id), r => Assert.Equal(OutboxStatus.Dispatched, r.Status));
        var held = await RowAsync(database, failing.Id, 2);
        Assert.Equal(OutboxStatus.Pending, held.Status);
        Assert.Equal(0, held.Attempts);

        Assert.Equal(businessNow, harness.BusinessClock.Now);
    }

    [Fact]
    public async Task Business_clock_frozen_infrastructure_clock_advancing_failing_handler_is_retried_and_polling_runs()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var clock = harness.InfrastructureClock;
        var frozen = harness.BusinessClock.Now;  // like DemoClock standing at 18:25
        var booking = await SeatWalkInAsync(harness, "T1");
        harness.Recorder.Fail(booking.Id.Value);

        await harness.Dispatcher.StartAsync(Ct);
        try
        {
            await EventuallyAsync(async () => (await RowAsync(database, booking.Id, 1)).Attempts == 1);

            // No signal from here on: only the poll timeout on the infrastructure clock wakes the dispatcher.
            await WaitForPollAsync(clock);
            clock.Advance(TimeSpan.FromSeconds(5));
            await EventuallyAsync(async () => (await RowAsync(database, booking.Id, 1)).Attempts == 2);

            await WaitForPollAsync(clock);
            clock.Advance(TimeSpan.FromSeconds(30));
            await EventuallyAsync(async () => (await RowAsync(database, booking.Id, 1)).Attempts == 3);

            harness.Recorder.Recover(booking.Id.Value);
            await WaitForPollAsync(clock);
            clock.Advance(TimeSpan.FromMinutes(2));
            await EventuallyAsync(async () => (await RowAsync(database, booking.Id, 1)).Status == OutboxStatus.Dispatched);

            await WaitForPollAsync(clock);
            clock.Advance(TimeSpan.FromSeconds(1));
            await EventuallyAsync(async () => (await RowAsync(database, booking.Id, 2)).Status == OutboxStatus.Dispatched);
        }
        finally
        {
            await harness.Dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.Equal(frozen, harness.BusinessClock.Now);
        var delivered = await RowAsync(database, booking.Id, 1);
        Assert.Equal(3, delivered.Attempts);
        Assert.Equal(clock.Now - TimeSpan.FromSeconds(1), delivered.DispatchedAt);
    }

    [Fact]
    public async Task Two_dispatchers_at_once_never_deliver_a_row_twice_and_keep_order_per_aggregate()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var ids = new List<Guid>();
        foreach (var table in Scenarios.FreeTablesForTwo)
        {
            await using var scope = harness.Scope();
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var booking = await Scenarios.SeatWalkInAsync(db, table, harness.BusinessClock.Now);
            await Scenarios.ClearAsync(db, booking.Id, harness.BusinessClock.Now.AddMinutes(50));
            ids.Add(booking.Id.Value);
        }

        var dispatchers = new[] { harness.Dispatcher, harness.NewDispatcher() };
        await Task.WhenAll(dispatchers.Select(d => Task.Run(async () =>
        {
            for (var i = 0; i < 100 && await PendingAsync(database) > 0; i++)
            {
                await d.DispatchBatchAsync(Ct);
            }
        })));

        Assert.Equal(0, await PendingAsync(database));
        var probe = harness.Recorder.For(ProbeHandler<BookingCreated>.Consumer);
        Assert.Equal(20, probe.Count);
        Assert.Equal(20, probe.Select(d => d.EventId).Distinct().Count());
        foreach (var id in ids)
        {
            Assert.Equal([1L, 2L, 3L, 4L], probe.Where(d => d.AggregateId == id).Select(d => d.Sequence));
        }
    }

    [Fact]
    public async Task Housekeeping_deletes_dispatched_rows_older_than_30_days()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        await SeatWalkInAsync(harness, "T1");
        await harness.DispatchAllAsync();
        var pending = await SeatWalkInAsync(harness, "T2");
        var now = harness.InfrastructureClock.Now;

        await using (var db = database.Context())
        {
            await db.Outbox.Where(x => x.Sequence == 1 && x.Status == OutboxStatus.Dispatched)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DispatchedAt, now - TimeSpan.FromDays(31)), Ct);
            await db.Outbox.Where(x => x.Sequence == 2 && x.Status == OutboxStatus.Dispatched)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DispatchedAt, now - TimeSpan.FromDays(29)), Ct);
        }

        var deleted = await harness.Services.GetRequiredService<OutboxHousekeeping>().PurgeAsync(Ct);

        Assert.Equal(1, deleted);
        var rows = await RowsAsync(database);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.AggregateId == pending.Id.Value && r.Status == OutboxStatus.Pending));
    }

    [Fact]
    public async Task Health_check_reports_outbox_lag_and_failed_rows()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var clock = harness.InfrastructureClock;
        var check = new OutboxHealthCheck(database.DataSource, clock);
        async Task<HealthStatus> StatusAsync() => (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status;

        Assert.Equal(HealthStatus.Healthy, await StatusAsync());

        var booking = await SeatWalkInAsync(harness, "T1");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Healthy, await StatusAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Degraded, await StatusAsync());

        await harness.DispatchAllAsync();
        Assert.Equal(HealthStatus.Healthy, await StatusAsync());

        await using (var db = database.Context())
        {
            await db.Outbox.Where(x => x.AggregateId == booking.Id.Value && x.Sequence == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, OutboxStatus.Failed), Ct);
        }

        Assert.Equal(HealthStatus.Degraded, await StatusAsync());
    }

    private static async Task<Booking> SeatWalkInAsync(EventHarness harness, string table)
    {
        await using var scope = harness.Scope();
        return await Scenarios.SeatWalkInAsync(scope.ServiceProvider.GetRequiredService<ReservationsDbContext>(), table, harness.BusinessClock.Now);
    }

    private static async Task<List<OutboxMessage>> RowsAsync(TestDatabase database, BookingId? aggregate = null)
    {
        await using var db = database.Context();
        var query = db.Outbox.AsNoTracking();
        if (aggregate is { } id)
        {
            query = query.Where(x => x.AggregateId == id.Value);
        }

        return await query.OrderBy(x => x.AggregateId).ThenBy(x => x.Sequence).ToListAsync(Ct);
    }

    private static async Task<OutboxMessage> RowAsync(TestDatabase database, BookingId aggregate, long sequence)
    {
        await using var db = database.Context();
        return await db.Outbox.AsNoTracking().SingleAsync(x => x.AggregateId == aggregate.Value && x.Sequence == sequence, Ct);
    }

    private static async Task<int> PendingAsync(TestDatabase database)
    {
        await using var db = database.Context();
        return await db.Outbox.CountAsync(x => x.Status == OutboxStatus.Pending, Ct);
    }

    /// <summary>Waits (in real time) until the dispatcher is idle on its poll timeout.</summary>
    private static Task WaitForPollAsync(TestClock clock) => EventuallyAsync(() => Task.FromResult(clock.PendingTimers > 0));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 15 s.");
            await Task.Delay(20, Ct);
        }
    }
}
