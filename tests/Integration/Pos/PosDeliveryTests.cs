using Microsoft.EntityFrameworkCore;
using Reservation.Contracts.Pos;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// <c>PosDeliveryWorker</c> (Design §6.2, §6.4, §6.7, §10.5; AC6.5, QA-5): per venue strictly in sequence order, one
/// sender at a time, retries while the POS is offline without counting attempts, no duplicate at the POS after a crash,
/// a rejection blocks the queue, and <c>posLogChanged</c> only after commit. Business clock fixed; only the
/// infrastructure clock moves.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PosDeliveryTests(PostgresFixture postgres) : PosTestBase(postgres)
{
    private static readonly string[] _threeTables = ["T1", "T2", "W1"];

    [Fact]
    public async Task QA5_offline_three_tables_wait_retry_every_5s_then_go_in_order_once_each()
    {
        await SetOnlineAsync(false);
        foreach (var table in _threeTables)
        {
            await WalkInAsync(table);
        }

        await DispatchAsync();
        PosLog.Clear();
        var queued = await PosRowsAsync();
        Assert.Equal([1L, 2L, 3L], queued.Select(r => r.VenueSequence));
        Assert.All(queued, r => Assert.Equal((PosMessageTypes.OpenTable, PosMessageStatus.Waiting), (r.MessageType, r.Status)));

        // Offline: only the head is tried; it waits 5 s; attempts stay 0; 42 and 43 wait behind 41.
        Assert.Equal(1, await Worker.DeliverOnceAsync(Ct));
        Assert.Equal(0, await Worker.DeliverOnceAsync(Ct)); // not due yet
        Infra.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await Worker.DeliverOnceAsync(Ct));
        var waiting = await PosRowsAsync();
        Assert.All(waiting, r => Assert.Equal((PosMessageStatus.Waiting, 0), (r.Status, r.Attempts)));
        Assert.Equal(PosMessage.Unavailable, waiting[0].LastError);
        Assert.Equal(Infra.GetUtcNow() + TimeSpan.FromSeconds(5), waiting[0].NextAttemptAt);
        Assert.Equal([queued[0].Id, queued[0].Id], Adapter.Calls.Select(c => c.Id));
        Assert.Empty(await ReceiptsAsync());
        Assert.Empty(PosLog.Ids); // an unavailable retry changes neither status nor attempts

        // Online: due at once, sent in sequence order, each accepted once.
        await SetOnlineAsync(true);
        await DeliverPosAsync();
        var sent = await PosRowsAsync();
        Assert.All(sent, r => Assert.Equal((PosMessageStatus.Sent, 1, DemoTime.Now), (r.Status, r.Attempts, r.DeliveredAt)));
        Assert.Equal(queued.Select(r => r.IdempotencyKey), (await ReceiptsAsync()).Select(r => r.IdempotencyKey));
        Assert.Equal(
            [queued[0].Id, queued[0].Id, queued[0].Id, queued[1].Id, queued[2].Id],
            Adapter.Calls.Select(c => c.Id));
        Assert.Equal(queued.Select(r => r.Id.Value), PosLog.Ids);
    }

    [Fact]
    public async Task Two_workers_never_send_for_one_venue_at_once_and_keep_the_order()
    {
        string[] tables = ["T1", "T2", "T3", "W1", "W2"];
        foreach (var table in tables)
        {
            await WalkInAsync(table);
        }

        await DispatchAsync();
        Adapter.Delay = TimeSpan.FromMilliseconds(30);
        var second = NewWorker();

        await Task.WhenAll(RunAsync(Worker), RunAsync(second), RunAsync(Worker), RunAsync(second));

        Assert.Equal(1, Adapter.MaxInFlight);
        var rows = await PosRowsAsync();
        Assert.All(rows, r => Assert.Equal(PosMessageStatus.Sent, r.Status));
        Assert.Equal(rows.Select(r => r.Id), Adapter.Calls.Select(c => c.Id)); // each sent once, in order
        Assert.Equal(rows.Select(r => r.IdempotencyKey), (await ReceiptsAsync()).Select(r => r.IdempotencyKey));

        static Task RunAsync(PosDeliveryWorker worker) => Task.Run(async () =>
        {
            for (var pass = 0; pass < 10; pass++)
            {
                await worker.DeliverOnceAsync(Ct);
            }
        });
    }

    [Fact]
    public async Task A_crash_after_the_POS_accepted_is_resent_and_ignored_at_the_POS()
    {
        await WalkInAsync("T1");
        await DispatchAsync();
        PosLog.Clear();
        using var crash = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Adapter.AfterSend = (_, _) =>
        {
            Adapter.AfterSend = null;
            crash.Cancel(); // the process dies after the POS accepted, before Sent is saved
            crash.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker.DeliverOnceAsync(crash.Token));
        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosMessageStatus.Waiting, 0), (row.Status, row.Attempts));
        Assert.Single(await ReceiptsAsync());
        Assert.Empty(PosLog.Ids);

        await NewWorker().DeliverOnceAsync(Ct); // restarted
        row = Assert.Single(await PosRowsAsync());
        Assert.Equal(PosMessageStatus.Sent, row.Status);
        Assert.Equal([row.IdempotencyKey, row.IdempotencyKey], Adapter.Calls.Select(c => c.IdempotencyKey));
        Assert.Single(await ReceiptsAsync()); // delivered once at the POS
    }

    [Fact]
    public async Task An_adapter_error_after_accepting_counts_an_attempt_and_the_retry_is_a_repeat_at_the_POS()
    {
        await WalkInAsync("T1");
        await DispatchAsync();
        Adapter.AfterSend = (_, _) =>
        {
            Adapter.AfterSend = null;
            throw new TimeoutException("the answer was lost");
        };

        await Worker.DeliverOnceAsync(Ct);
        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosMessageStatus.Waiting, 1, "adapter_exception:TimeoutException"), (row.Status, row.Attempts, row.LastError));
        Assert.Equal(Infra.GetUtcNow() + TimeSpan.FromSeconds(5), row.NextAttemptAt);

        Infra.Advance(TimeSpan.FromSeconds(5));
        await Worker.DeliverOnceAsync(Ct);
        var sent = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosMessageStatus.Sent, 2), (sent.Status, sent.Attempts));
        Assert.Single(await ReceiptsAsync());
    }

    [Fact]
    public async Task A_rejected_message_fails_and_blocks_the_venues_queue()
    {
        await WalkInAsync("T1");
        await WalkInAsync("T2");
        await DispatchAsync();
        PosLog.Clear();
        var first = (await PosRowsAsync())[0];
        Adapter.Override = m => Task.FromResult(m.Id == first.Id ? PosSendResult.Rejected("unknown_table") : PosSendResult.Accepted());

        await DeliverPosAsync();
        Infra.Advance(TimeSpan.FromMinutes(1));
        await DeliverPosAsync();

        var rows = await PosRowsAsync();
        Assert.Equal((PosMessageStatus.Failed, "unknown_table"), (rows[0].Status, rows[0].LastError));
        Assert.Equal(PosMessageStatus.Waiting, rows[1].Status);
        Assert.Equal([first.Id], Adapter.Calls.Select(c => c.Id)); // 2 never sent ahead of 1
        Assert.Equal([first.Id.Value], PosLog.Ids);
    }

    [Fact]
    public async Task A_POS_unavailable_for_24_hours_fails_the_message()
    {
        await SetOnlineAsync(false);
        await WalkInAsync("T1");
        await DispatchAsync();

        await Worker.DeliverOnceAsync(Ct);
        Infra.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await Worker.DeliverOnceAsync(Ct);

        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal((PosMessageStatus.Failed, 0, PosMessage.UnavailableTooLong), (row.Status, row.Attempts, row.LastError));
    }

    [Fact]
    public async Task PosLogChanged_is_pushed_only_after_the_commit()
    {
        await WalkInAsync("T1");
        await DispatchAsync();
        var row = Assert.Single(await PosRowsAsync());
        Assert.Equal([row.Id.Value], PosLog.Ids); // queued: pushed after the dispatcher's commit
        PosLog.Clear();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Adapter.AfterSend = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        var delivering = Worker.DeliverOnceAsync(Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(PosLog.Ids);
        release.SetResult();
        await delivering;

        var change = Assert.Single(PosLog.Changes);
        Assert.Equal((VenueId, row.Id.Value), (change.VenueId.Value, Assert.Single(change.PosMessageIds)));
    }

    [Fact]
    public async Task The_online_switch_is_stored_and_survives_a_restart()
    {
        await SetOnlineAsync(false);

        await using var restarted = new ApiFactory(Database.ConnectionString);
        using var presenter = restarted.CreateApiClient();
        await SignInAsync(presenter, ApiFactory.PresenterPasscode);
        var status = await ReadAsync<PosStatus>(await presenter.GetAsync($"{Venue}/pos/status", Ct));
        Assert.False(status.Online);

        await using (var db = Database.Context())
        {
            Assert.False(await db.MockPosState.Select(s => s.Online).SingleAsync(Ct));
        }
    }

    private sealed record PosStatus(bool Online);
}
