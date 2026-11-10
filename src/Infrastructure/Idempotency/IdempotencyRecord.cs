using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Idempotency;

/// <summary>
/// One <c>Idempotency-Key</c> seen on a create request (ADR-004, Design §13 <c>idempotency_keys</c>). Written in the
/// same transaction as the record it created, so a key exists only for a request that succeeded. Kept 24 hours.
/// </summary>
public sealed class IdempotencyRecord
{
    public required VenueId VenueId { get; init; }

    /// <summary>The operation the key belongs to, e.g. <c>create-online-booking</c>.</summary>
    public required string Operation { get; init; }

    public required string Key { get; init; }

    /// <summary>SHA-256 of the request body; the same key with another body is refused.</summary>
    public required string RequestHash { get; init; }

    /// <summary>The HTTP status of the first response; 0 until the request has finished.</summary>
    public int StatusCode { get; init; }

    /// <summary>The first response body (JSON), replayed for a repeat.</summary>
    public required string Response { get; init; }

    /// <summary>Infrastructure clock (Design §5.6).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
