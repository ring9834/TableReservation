using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Pos;
using Reservation.Api.Features.Seating;
using Reservation.Contracts.Pos;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Pos;

/// <summary>
/// Shared steps for the POS tests (Design §6, §6.7): the business clock stays at 18:25 and only the infrastructure clock
/// (<see cref="Infra"/>) moves. The adapter is the real <see cref="MockPosAdapter"/> behind <see cref="TestPosAdapter"/>,
/// which records calls and can be told to misbehave; <c>posLogChanged</c> pushes are recorded by <see cref="PosLog"/>.
/// Workers are driven by hand.
/// </summary>
public abstract class PosTestBase(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    public const string WebhookPath = "/api/v1/webhooks/pos";

    protected TestClock Infra { get; } = new(DemoTime.InfrastructureStart);

    protected override TestClock? InfrastructureClock => Infra;

    protected RecordingPosLogNotifier PosLog { get; } = new();

    protected TestPosAdapter Adapter => Factory.Services.GetRequiredService<TestPosAdapter>();

    protected PosDeliveryWorker Worker => Factory.Services.GetRequiredService<PosDeliveryWorker>();

    protected static Guid VenueId => GoldenDragon.VenueId.Value;

    /// <summary>The presenter's client, signed in on first use (demo-login allows 5 sign-ins a minute).</summary>
    protected HttpClient Presenter => _presenter ?? throw new InvalidOperationException("Call PresenterClientAsync first.");

    private HttpClient? _presenter;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync(Client);
    }

    public override async ValueTask DisposeAsync()
    {
        _presenter?.Dispose();
        await base.DisposeAsync();
    }

    protected async Task<HttpClient> PresenterClientAsync() => _presenter ??= await PresenterAsync();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPosLogNotifier>(PosLog);
        services.AddSingleton<TestPosAdapter>();
        services.AddSingleton<IPosAdapter>(provider => provider.GetRequiredService<TestPosAdapter>());
    }

    /// <summary>A second, independent worker sharing the API's services (two API instances).</summary>
    protected PosDeliveryWorker NewWorker() => new(
        Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        Factory.Services.GetRequiredService<PosSignal>(),
        Factory.Services.GetRequiredService<IOptions<PosOptions>>(),
        Factory.Services.GetRequiredService<IOptions<OutboxOptions>>(),
        Factory.Clock,
        Infra,
        Factory.Services.GetRequiredService<ILogger<PosDeliveryWorker>>());

    /// <summary>Delivery passes until one tries nothing.</summary>
    protected async Task DeliverPosAsync()
    {
        while (await Worker.DeliverOnceAsync(Ct) > 0)
        {
        }
    }

    protected async Task SetOnlineAsync(bool online)
    {
        var presenter = await PresenterClientAsync();
        var response = await presenter.PutAsJsonAsync("/api/v1/demo/pos/online", new { online }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private protected async Task<SeatWalkInResponse> WalkInAsync(string table, string? name = null, int partySize = 2) =>
        await ReadAsync<SeatWalkInResponse>(
            await PostAsync(Client, $"{Venue}/walk-ins", new { partySize, tableId = Table(table), name }), HttpStatusCode.Created);

    private protected async Task<CreateBookingResponse> PhoneBookingAsync(int hour, int minute, int partySize, string name, string mobile) =>
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/phone-bookings", BookingBody(DemoTime.Tonight, hour, minute, partySize, name, mobile)),
            HttpStatusCode.Created);

    private protected async Task<BookingStateResponse> SeatAsync(Guid bookingId)
    {
        var version = await VersionAsync(bookingId);
        return await ReadAsync<BookingStateResponse>(await PostAsync(Client, $"{Venue}/bookings/{bookingId}/seat", new { version }));
    }

    private protected async Task<BookingStateResponse> ClearAsync(string table, Guid bookingId)
    {
        var version = await VersionAsync(bookingId);
        return await ReadAsync<BookingStateResponse>(
            await PostAsync(Client, $"{Venue}/tables/{Table(table)}/clear", new { bookingId, version }));
    }

    private protected async Task<SimulateBillPaidResponse> BillPaidAsync(string table, long totalCents) =>
        await ReadAsync<SimulateBillPaidResponse>(
            await PostAsync(Client, "/api/v1/demo/pos/bill-paid", new { tableId = Table(table), totalCents }));

    protected async Task<Guid> BookingIdAsync(string reference)
    {
        await using var db = Database.Context();
        var parsed = BookingReference.Parse(reference).Value;
        return (await db.Bookings.AsNoTracking().SingleAsync(b => b.Reference == parsed, Ct)).Id.Value;
    }

    protected async Task<uint> VersionAsync(Guid bookingId)
    {
        await using var db = Database.Context();
        var id = new BookingId(bookingId);
        return (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == id, Ct)).Version;
    }

    /// <summary>The venue's POS rows in sequence order.</summary>
    protected async Task<List<PosMessage>> PosRowsAsync()
    {
        await using var db = Database.Context();
        return await db.PosMessages.AsNoTracking().OrderBy(m => m.VenueSequence).ToListAsync(Ct);
    }

    /// <summary>What the mock POS accepted, in the order it accepted them.</summary>
    protected async Task<List<MockPosReceipt>> ReceiptsAsync()
    {
        await using var db = Database.Context();
        return await db.MockPosReceipts.AsNoTracking().OrderBy(r => r.ReceivedOrder).ToListAsync(Ct);
    }

    /// <summary>A <c>BillClosed</c> as the POS would send it; <paramref name="bookingId"/> is the echo of §6.5 step 1.</summary>
    protected static PosBillClosed Bill(
        string table, long totalCents, Guid? bookingId = null, string? posBillId = null, DateTimeOffset? closedAt = null, Guid? messageId = null) =>
        new()
        {
            MessageId = messageId ?? Guid.NewGuid(),
            MessageType = PosMessageTypes.BillClosed,
            VenueId = VenueId,
            BookingId = bookingId,
            TableIds = [Table(table)],
            OccurredAt = closedAt ?? DemoTime.Now,
            PosBillId = posBillId ?? $"bill-test-{Guid.NewGuid():N}",
            TableId = Table(table),
            BillTotalCents = totalCents,
            ClosedAt = closedAt ?? DemoTime.Now,
        };

    protected static string Json(PosBillClosed bill) => JsonSerializer.Serialize(bill, PosJson.Options);

    /// <summary>Posts a body to the POS webhook from an anonymous client; signed with the test secret unless told otherwise.</summary>
    protected async Task<HttpResponseMessage> PostWebhookAsync(string body, string? signature = null, bool sign = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var header = signature ?? (sign ? PosWebhookSigner.Sign(body, ApiFactory.PosWebhookSecret, Infra.GetUtcNow()) : null);
        if (header is not null)
        {
            request.Headers.Add(PosWebhookSigner.Header, header);
        }

        return await NewClient().SendAsync(request, Ct);
    }
}

/// <summary>
/// The real mock POS, with a call log and knobs: <see cref="Override"/> replaces the answer, <see cref="AfterSend"/> runs
/// after the mock answered (e.g. a crash after it accepted), and <see cref="MaxInFlight"/> shows whether two sends ever
/// overlapped.
/// </summary>
public sealed class TestPosAdapter(MockPosAdapter mock) : IPosAdapter
{
    private readonly ConcurrentQueue<PosOutbound> _calls = new();
    private int _inFlight;
    private int _maxInFlight;

    public Func<PosOutbound, Task<PosSendResult>>? Override { get; set; }

    public Func<PosOutbound, PosSendResult, Task>? AfterSend { get; set; }

    public TimeSpan Delay { get; set; }

    public IReadOnlyList<PosOutbound> Calls => [.. _calls];

    public int MaxInFlight => _maxInFlight;

    public async Task<PosSendResult> SendAsync(PosOutbound message, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, now);
        try
        {
            _calls.Enqueue(message);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var result = Override is { } replace ? await replace(message) : await mock.SendAsync(message, cancellationToken);
            if (AfterSend is { } after)
            {
                await after(message, result);
            }

            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public Result<PosInbound> ParseInbound(string body, string? signatureHeader) => mock.ParseInbound(body, signatureHeader);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>The POS log changes <see cref="PosMessageChangeInterceptor"/> handed on after each commit.</summary>
public sealed class RecordingPosLogNotifier : IPosLogNotifier
{
    private readonly ConcurrentQueue<PosLogChanges> _changes = new();

    public IReadOnlyList<PosLogChanges> Changes => [.. _changes];

    public IReadOnlyList<Guid> Ids => [.. _changes.SelectMany(c => c.PosMessageIds)];

    public void Clear() => _changes.Clear();

    public Task NotifyAsync(PosLogChanges changes, CancellationToken cancellationToken)
    {
        _changes.Enqueue(changes);
        return Task.CompletedTask;
    }
}
