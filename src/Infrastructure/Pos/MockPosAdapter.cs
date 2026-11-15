using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Reservation.Contracts.Pos;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// The demo's POS (Design §6.1, §6.7, §17). It behaves like an external system: every call opens its own scope, so its
/// own <see cref="ReservationsDbContext"/>, connection and transaction, and what it records is committed whatever the
/// caller's transaction does afterwards. Its state lives in the database, so it survives restarts:
/// <list type="bullet">
/// <item><c>mock_pos_state</c>: the online switch (no row = online; the demo reset empties it).</item>
/// <item><c>mock_pos_received</c>: every idempotency key accepted, in order. A key already there is a repeat: accepted
/// again and ignored, so a resend after a crash is delivered once.</item>
/// <item><c>mock_pos_open_tables</c>: the booking each <c>OpenTable</c> opened on each table, so a simulated
/// <c>BillClosed</c> can echo the <c>bookingId</c> (§6.5 step 1).</item>
/// </list>
/// Inbound webhooks are checked with <see cref="PosWebhookSigner"/> and <c>Pos:WebhookSecret</c>.
/// </summary>
public sealed class MockPosAdapter(
    IServiceScopeFactory scopeFactory,
    IOptions<PosOptions> options,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
    : IPosAdapter
{
    public const string InvalidSignature = "invalid_signature";
    public const string NotConfigured = "webhook_secret_missing";
    public const string InvalidPayload = "invalid_payload";

    /// <summary>Longest POS bill ID accepted.</summary>
    public const int MaxBillIdLength = 100;

    public async Task<PosSendResult> SendAsync(PosOutbound message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        if (!await IsOnlineAsync(db, cancellationToken))
        {
            return PosSendResult.Unavailable();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var receivedAt = clock.GetUtcNow().ToUniversalTime();
        var venueId = message.VenueId.Value;
        var bookingId = message.BookingId?.Value;
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO mock_pos_received (idempotency_key, venue_id, message_type, booking_id, received_at)
            VALUES ({message.IdempotencyKey}, {venueId}, {message.MessageType}, {bookingId}, {receivedAt})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PosSendResult.Accepted(duplicate: true);
        }

        if (message.MessageType == PosMessageTypes.OpenTable && bookingId is { } opened)
        {
            foreach (var tableId in TableIds(message.Payload))
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO mock_pos_open_tables (table_id, venue_id, booking_id, opened_at)
                    VALUES ({tableId}, {venueId}, {opened}, {receivedAt})
                    ON CONFLICT (table_id) DO UPDATE SET venue_id = excluded.venue_id, booking_id = excluded.booking_id, opened_at = excluded.opened_at
                    """, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return PosSendResult.Accepted();
    }

    public Result<PosInbound> ParseInbound(string body, string? signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(body);
        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.WebhookSecret))
        {
            return Error.Forbidden(NotConfigured);
        }

        if (!PosWebhookSigner.Verify(body, signatureHeader, settings.WebhookSecret, clock.GetUtcNow(), settings.SignatureTolerance))
        {
            return Error.Forbidden(InvalidSignature);
        }

        try
        {
            var bill = JsonSerializer.Deserialize<PosBillClosed>(body, PosJson.Options);
            return bill is not null && IsValid(bill) ? new PosInbound(bill, body) : Error.Validation("body", InvalidPayload);
        }
        catch (JsonException)
        {
            return Error.Validation("body", InvalidPayload);
        }
    }

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await IsOnlineAsync(scope.ServiceProvider.GetRequiredService<ReservationsDbContext>(), cancellationToken);
    }

    /// <summary>Switches the mock online or offline; stored, so it survives a restart.</summary>
    public async Task SetOnlineAsync(bool online, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var changedAt = clock.GetUtcNow().ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO mock_pos_state (id, online, changed_at) VALUES ({MockPosState.SingletonId}, {online}, {changedAt})
            ON CONFLICT (id) DO UPDATE SET online = excluded.online, changed_at = excluded.changed_at
            """, cancellationToken);
    }

    /// <summary>
    /// What the POS sends when a bill on <paramref name="tableId"/> is paid ("Bill paid", AC6.6): a signed
    /// <c>BillClosed</c> with a new message and bill ID, echoing the booking the mock opened on that table if any. The
    /// caller hands body and signature to the webhook processor, then calls <see cref="CloseTableAsync"/>.
    /// </summary>
    public async Task<SimulatedBill> SimulateBillClosedAsync(
        VenueId venueId, TableId tableId, long totalCents, DateTimeOffset closedAt, CancellationToken cancellationToken)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException("Pos:WebhookSecret must be set to simulate a POS bill.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var openBooking = await db.MockPosOpenTables.AsNoTracking()
            .Where(t => t.TableId == tableId && t.VenueId == venueId)
            .Select(t => (BookingId?)t.BookingId)
            .SingleOrDefaultAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var bill = new PosBillClosed
        {
            MessageId = Guid.CreateVersion7(now),
            MessageType = PosMessageTypes.BillClosed,
            VenueId = venueId.Value,
            BookingId = openBooking?.Value,
            TableIds = [tableId.Value],
            OccurredAt = closedAt.ToUniversalTime(),
            PosBillId = $"bill-{Guid.CreateVersion7(now):N}",
            TableId = tableId.Value,
            BillTotalCents = totalCents,
            ClosedAt = closedAt.ToUniversalTime(),
        };
        var body = JsonSerializer.Serialize(bill, PosJson.Options);
        return new SimulatedBill(bill, body, PosWebhookSigner.Sign(body, secret, now));
    }

    /// <summary>The POS closed the table with its bill: forget which booking was open on it.</summary>
    public async Task CloseTableAsync(TableId tableId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await db.MockPosOpenTables.Where(t => t.TableId == tableId).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<bool> IsOnlineAsync(ReservationsDbContext db, CancellationToken cancellationToken) =>
        await db.MockPosState.AsNoTracking()
            .Where(s => s.Id == MockPosState.SingletonId)
            .Select(s => (bool?)s.Online)
            .SingleOrDefaultAsync(cancellationToken) ?? true;

    private static IEnumerable<Guid> TableIds(JsonDocument payload) =>
        payload.RootElement.TryGetProperty("tableIds", out var tables) && tables.ValueKind == JsonValueKind.Array
            ? tables.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetGuid()).ToList()
            : [];

    private static bool IsValid(PosBillClosed bill) =>
        bill.Version == PosEnvelope.CurrentVersion
        && bill.MessageType == PosMessageTypes.BillClosed
        && bill.MessageId != Guid.Empty
        && bill.VenueId != Guid.Empty
        && bill.TableId != Guid.Empty
        && !string.IsNullOrWhiteSpace(bill.PosBillId) && bill.PosBillId.Length <= MaxBillIdLength
        && bill.BillTotalCents >= 0
        && bill.TipCents is null or >= 0;
}

/// <summary>A simulated <c>BillClosed</c>: the message, its JSON body and its <c>Pos-Signature</c> header.</summary>
public sealed record SimulatedBill(PosBillClosed Bill, string Body, string Signature);
