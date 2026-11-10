using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Idempotency;

/// <summary>
/// Idempotency keys for create requests (ADR-004, Design §14.1). All calls run on the caller's
/// <see cref="ReservationsDbContext"/> inside its open transaction:
/// <list type="number">
/// <item><see cref="TryClaimAsync"/> inserts the key row first. A concurrent request with the same key blocks on the
/// primary key until the first commits (then sees its row) or rolls back (then claims the key itself).</item>
/// <item>After <c>SaveChanges</c>, <see cref="CompleteAsync"/> stores the real response, with the row version read back.</item>
/// </list>
/// A failed request rolls back, so its key is not kept. Keys expire after <see cref="Retention"/> on the infrastructure clock.
/// </summary>
public sealed class IdempotencyStore(ReservationsDbContext db, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock)
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// Claims <paramref name="key"/> for this request. Returns <c>null</c> when claimed; otherwise the stored record of
    /// the earlier request, whose <see cref="IdempotencyRecord.RequestHash"/> the caller compares.
    /// </summary>
    public async Task<IdempotencyRecord?> TryClaimAsync(
        VenueId venueId, string operation, string key, string requestHash, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Claim an idempotency key inside the command's transaction.");
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var cutoff = now - Retention;
        var venue = venueId.Value;

        // An expired key is free again.
        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM idempotency_keys WHERE venue_id = {venue} AND operation = {operation} AND key = {key} AND created_at < {cutoff}",
            cancellationToken);

        var inserted = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO idempotency_keys (venue_id, operation, key, request_hash, status_code, response, created_at) VALUES ({venue}, {operation}, {key}, {requestHash}, 0, CAST('{{}}' AS jsonb), {now}) ON CONFLICT DO NOTHING",
            cancellationToken);
        if (inserted == 1)
        {
            return null;
        }

        return await db.IdempotencyKeys.AsNoTracking()
            .SingleAsync(x => x.VenueId == venueId && x.Operation == operation && x.Key == key, cancellationToken);
    }

    /// <summary>Stores the response of the request that claimed the key, in the same transaction.</summary>
    public async Task CompleteAsync(
        VenueId venueId, string operation, string key, int statusCode, string responseJson, CancellationToken cancellationToken)
    {
        var venue = venueId.Value;
        var updated = await db.Database.ExecuteSqlAsync(
            $"UPDATE idempotency_keys SET status_code = {statusCode}, response = CAST({responseJson} AS jsonb) WHERE venue_id = {venue} AND operation = {operation} AND key = {key}",
            cancellationToken);
        if (updated != 1)
        {
            throw new InvalidOperationException("The idempotency key was not claimed by this request.");
        }
    }
}
