using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Messaging;

/// <summary>
/// <see cref="MessageDeliveryWorker"/> and <see cref="MessageChangeInterceptor"/> (Design §7.1, §7.2) against a real
/// database. The business clock stays at 18:25 throughout; only the infrastructure clock moves, so these tests also
/// show that texts go out, retry and expire while the demo clock stands still.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageDeliveryWorkerTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_goes_Queued_Sent_Delivered_with_the_business_clock_fixed()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database); // the real PhonePanelSender
        var businessNow = harness.BusinessClock.Now;
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);

        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

        var sent = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal(businessNow, sent.SentAt);
        Assert.Equal(PhonePanelSender.ReferencePrefix + queued.Id.Value, sent.ProviderRef);
        Assert.Equal(harness.InfrastructureClock.Now.AddSeconds(1), sent.DeliveryDueAt);
        await using (var db = database.Context())
        {
            var outbox = await db.Outbox.SingleAsync(o => o.AggregateId == queued.Id.Value, Ct);
            Assert.Equal("MessageSent", outbox.EventType);
        }

        Assert.Equal(0, await harness.MessageWorker.DeliverBatchAsync(Ct)); // delivery not due yet
        harness.InfrastructureClock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

        var delivered = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Delivered, delivered.Status);
        Assert.Equal(businessNow, delivered.DeliveredAt);
        Assert.Null(delivered.DeliveryDueAt);
        Assert.Equal(businessNow, harness.BusinessClock.Now);
    }

    [Fact]
    public async Task Temporary_errors_back_off_5s_30s_2m_10m_then_every_10m()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender { Behaviour = (_, _) => Task.FromResult(SendResult.Temporary("timeout")) };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);

        foreach (var (seconds, attempt) in new[] { 5, 30, 120, 600, 600 }.Select((s, i) => (s, i + 1)))
        {
            var now = harness.InfrastructureClock.Now;
            Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

            var message = await LoadAsync(database, queued.Id);
            Assert.Equal(MessageStatus.Queued, message.Status);
            Assert.Equal(attempt, message.Attempts);
            Assert.Equal(now.AddSeconds(seconds), message.NextAttemptAt);
            Assert.Equal("timeout", message.LastError);

            harness.InfrastructureClock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.Equal(0, await harness.MessageWorker.DeliverBatchAsync(Ct)); // not due yet
            harness.InfrastructureClock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(5, sender.Calls.Count);
    }

    [Fact]
    public async Task A_table_ready_text_not_sent_within_10_minutes_expires()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender();
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.TableReady);

        harness.InfrastructureClock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

        var message = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal(MessageDeliveryWorker.Expired, message.LastError);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task Retries_of_a_table_ready_text_stop_at_its_expiry()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender { Behaviour = (_, _) => Task.FromResult(SendResult.Temporary("throttled")) };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.TableReady);

        // Attempts at 0 s, 5 s, 35 s and 2 min 35 s; the next would be at 12 min 35 s, past the 10-minute expiry.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));
            var message = await LoadAsync(database, queued.Id);
            if (message.Status == MessageStatus.Queued)
            {
                harness.InfrastructureClock.Set(message.NextAttemptAt);
            }
        }

        var failed = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Failed, failed.Status);
        Assert.Equal(4, failed.Attempts);
        harness.InfrastructureClock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await harness.MessageWorker.DeliverBatchAsync(Ct));
        Assert.Equal(4, sender.Calls.Count);
    }

    [Fact]
    public async Task A_permanent_error_fails_at_once_and_is_never_retried()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender { Behaviour = (_, _) => Task.FromResult(SendResult.Permanent("invalid_number")) };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);

        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));
        harness.InfrastructureClock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await harness.MessageWorker.DeliverBatchAsync(Ct));

        var message = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal("invalid_number", message.LastError);
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task A_sender_that_throws_is_retried_and_only_the_exception_type_is_kept()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender { Behaviour = (m, _) => throw new HttpRequestException($"provider said no to {m.To}") };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);

        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

        var message = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Queued, message.Status);
        Assert.Equal(MessageDeliveryWorker.SenderErrorPrefix + nameof(HttpRequestException), message.LastError);
        Assert.DoesNotContain("+64", string.Join(" ", harness.Logs.Lines.Select(l => l.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_workers_at_once_never_send_a_message_twice()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var sender = new FakeSender
        {
            Behaviour = async (m, ct) =>
            {
                await Task.Delay(20, ct);
                return SendResult.Accepted("prov-" + m.ClientReference);
            },
        };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var ids = new List<MessageId>();
        for (var i = 0; i < 25; i++)
        {
            ids.Add((await QueueAsync(harness, MessagePurpose.Confirmation)).Id);
        }

        var first = harness.MessageWorker;
        var second = harness.NewMessageWorker();
        async Task RunAsync(MessageDeliveryWorker worker)
        {
            while (await worker.DeliverBatchAsync(Ct) > 0)
            {
            }
        }

        await Task.WhenAll(RunAsync(first), RunAsync(second), RunAsync(first), RunAsync(second));

        Assert.Equal(ids.Count, sender.Calls.Count);
        Assert.Equal(ids.Select(i => i.Value.ToString()).Order(), sender.Calls.Select(c => c.ClientReference).Order());
        await using var db = database.Context();
        Assert.Equal(ids.Count, await db.Messages.CountAsync(m => m.Status == MessageStatus.Sent, Ct));
    }

    [Fact]
    public async Task A_crash_after_sending_rolls_back_and_the_retry_reuses_the_same_reference()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        using var crash = new CancellationTokenSource();
        var sender = new FakeSender();
        sender.Behaviour = (m, _) =>
        {
            if (sender.Calls.Count == 1)
            {
                crash.Cancel(); // the process "dies" after the provider accepted, before the commit
            }

            return Task.FromResult(SendResult.Accepted("prov-" + m.ClientReference));
        };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);
        harness.Notifier.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.MessageWorker.DeliverBatchAsync(crash.Token));

        var afterCrash = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Queued, afterCrash.Status);
        Assert.Equal(0, afterCrash.Attempts);
        Assert.Empty(harness.Notifier.Changes); // nothing was committed, so nothing was pushed

        Assert.Equal(1, await harness.NewMessageWorker().DeliverBatchAsync(Ct));

        Assert.Equal(2, sender.Calls.Count);
        Assert.All(sender.Calls, c => Assert.Equal(queued.Id.Value.ToString(), c.ClientReference));
        var sent = await LoadAsync(database, queued.Id);
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal("prov-" + queued.Id.Value, sent.ProviderRef);
    }

    [Fact]
    public async Task Changes_are_pushed_only_after_the_batch_commits()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new FakeSender
        {
            Behaviour = async (m, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return SendResult.Accepted("prov-" + m.ClientReference);
            },
        };
        await using var harness = EventHarness.Create(database, s => s.AddSingleton<IMessageSender>(sender));
        var queued = await QueueAsync(harness, MessagePurpose.TableReady);
        Assert.Contains(queued.Id.Value, harness.Notifier.MessageIds); // queuing committed: pushed
        harness.Notifier.Clear();

        var batch = harness.MessageWorker.DeliverBatchAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(harness.Notifier.Changes);

        release.SetResult();
        Assert.Equal(1, await batch);

        var change = Assert.Single(harness.Notifier.Changes);
        Assert.Equal(GoldenDragon.VenueId, change.VenueId);
        Assert.Equal([queued.Id.Value], change.MessageIds);
        Assert.Equal([queued.SubjectId], change.TableReadyEntryIds);
    }

    [Fact]
    public async Task A_handler_that_fails_after_queuing_pushes_nothing_while_the_others_in_the_batch_still_do()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        var probe = new SavepointProbe();
        await using var harness = EventHarness.Create(database, s =>
        {
            s.AddSingleton(probe);
            s.AddEventHandler<WaitlistJoined, KeptBeforeHandler>();
            s.AddEventHandler<WaitlistJoined, QueueThenFailHandler>();
            s.AddEventHandler<WaitlistJoined, KeptAfterHandler>();
        });
        await using (var scope = harness.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            var guest = await db.Guests.OrderBy(g => g.Id).FirstAsync(Ct);
            db.WaitlistEntries.Add(WaitlistEntry.Join(GoldenDragon.VenueId, guest.Id, guest.Name, PartySize.Of(4), 30, harness.BusinessClock.Now).Value);
            await db.SaveChangesAsync(Ct);
        }

        Assert.False(await harness.MessageSignal.WaitAsync(TimeSpan.Zero, Ct));

        await harness.DispatchAllAsync();

        Assert.NotNull(probe.Lost);
        await using (var db = database.Context())
        {
            var ids = await db.Messages.Select(m => m.Id).ToListAsync(Ct);
            Assert.Equal(new[] { probe.KeptBefore!.Value.Value, probe.KeptAfter!.Value.Value }.Order(), ids.Select(i => i.Value).Order());
        }

        Assert.Equal(new[] { probe.KeptBefore!.Value.Value, probe.KeptAfter!.Value.Value }.Order(), harness.Notifier.MessageIds.Order());
        Assert.DoesNotContain(probe.Lost!.Value.Value, harness.Notifier.MessageIds);
        Assert.True(await harness.MessageSignal.WaitAsync(TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task Delivery_survives_a_restart()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var queued = await QueueAsync(harness, MessagePurpose.Confirmation);
        Assert.Equal(1, await harness.MessageWorker.DeliverBatchAsync(Ct));

        // A new worker (after a restart) finds the Sent row whose delivery is due.
        harness.InfrastructureClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await harness.NewMessageWorker().DeliverBatchAsync(Ct));

        Assert.Equal(MessageStatus.Delivered, (await LoadAsync(database, queued.Id)).Status);
    }

    [Fact]
    public async Task A_queued_message_wakes_the_worker_through_the_signal()
    {
        await using var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = EventHarness.Create(database);
        var worker = harness.MessageWorker;
        await worker.StartAsync(Ct);
        try
        {
            // The infrastructure clock is a TestClock that never moves here, so the 1 s poll never fires:
            // only the signal rung after the commit can wake the worker.
            await WaitUntilAsync(() => harness.InfrastructureClock.PendingTimers > 0);
            var queued = await QueueAsync(harness, MessagePurpose.Confirmation);

            await WaitUntilAsync(async () => (await LoadAsync(database, queued.Id)).Status == MessageStatus.Sent);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    private static async Task<Message> QueueAsync(EventHarness harness, MessagePurpose purpose)
    {
        await using var scope = harness.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var guest = await db.Guests.OrderBy(g => g.Id).FirstAsync(Ct);
        var message = Message.Queue(
            GoldenDragon.VenueId, guest.Id, purpose, MessageChannel.Sms, Language.En, "Golden Dragon: test text", [],
            Guid.CreateVersion7(), harness.BusinessClock.Now, harness.InfrastructureClock.Now);
        db.Messages.Add(message);
        await db.SaveChangesAsync(Ct);
        return message;
    }

    private static async Task<Message> LoadAsync(TestDatabase database, MessageId id)
    {
        await using var db = database.Context();
        return await db.Messages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 10 s.");
            await Task.Delay(20, Ct);
        }
    }
}

/// <summary>A sender whose answer each test chooses; records every call.</summary>
public sealed class FakeSender : IMessageSender
{
    private readonly ConcurrentQueue<OutgoingMessage> _calls = new();

    public Func<OutgoingMessage, CancellationToken, Task<SendResult>> Behaviour { get; set; } =
        (m, _) => Task.FromResult(SendResult.Accepted("prov-" + m.ClientReference));

    public IReadOnlyList<OutgoingMessage> Calls => [.. _calls];

    public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        _calls.Enqueue(message);
        return Behaviour(message, cancellationToken);
    }
}

/// <summary>The IDs of the messages the savepoint test's handlers queued.</summary>
public sealed class SavepointProbe
{
    public MessageId? KeptBefore { get; set; }

    public MessageId? Lost { get; set; }

    public MessageId? KeptAfter { get; set; }
}

/// <summary>Queues one text for the joined entry; the decorator saves it in the handler's savepoint.</summary>
public abstract class QueueingHandler(ReservationsDbContext db, TimeProvider clock, TimeProvider infrastructureClock) : IEventHandler<WaitlistJoined>
{
    public abstract string ConsumerName { get; }

    public virtual Task HandleAsync(WaitlistJoined domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        Queue(domainEvent);
        return Task.CompletedTask;
    }

    protected MessageId Queue(WaitlistJoined domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var message = Message.Queue(
            domainEvent.VenueId, domainEvent.GuestId, MessagePurpose.TableReady, MessageChannel.Sms, Language.En,
            $"Golden Dragon: {ConsumerName}", [], domainEvent.WaitlistEntryId.Value, clock.GetUtcNow(), infrastructureClock.GetUtcNow());
        db.Messages.Add(message);
        return message.Id;
    }
}

public sealed class KeptBeforeHandler(
    ReservationsDbContext db, TimeProvider clock, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock, SavepointProbe probe)
    : QueueingHandler(db, clock, infrastructureClock)
{
    public override string ConsumerName => "kept-before";

    public override Task HandleAsync(WaitlistJoined domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        probe.KeptBefore = Queue(domainEvent);
        return Task.CompletedTask;
    }
}

public sealed class KeptAfterHandler(
    ReservationsDbContext db, TimeProvider clock, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock, SavepointProbe probe)
    : QueueingHandler(db, clock, infrastructureClock)
{
    public override string ConsumerName => "kept-after";

    public override Task HandleAsync(WaitlistJoined domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        probe.KeptAfter = Queue(domainEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Queues a text, saves it, then fails: its savepoint is rolled back, so the text never existed.</summary>
public sealed class QueueThenFailHandler(
    ReservationsDbContext db, TimeProvider clock, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock, SavepointProbe probe)
    : QueueingHandler(db, clock, infrastructureClock)
{
    private readonly ReservationsDbContext _db = db;

    public override string ConsumerName => "queue-then-fail";

    public override async Task HandleAsync(WaitlistJoined domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        probe.Lost = Queue(domainEvent);
        await _db.SaveChangesAsync(cancellationToken);
        throw new InvalidOperationException("Simulated failure after saving a message.");
    }
}
