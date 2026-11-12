using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Idempotency;

namespace Reservation.Api.Common;

/// <summary>
/// The optional <c>Idempotency-Key</c> for host creates (walk-in, waitlist join; Design §14.2), stored like the booking
/// creates (§14.1). Call <see cref="TryReplayAsync{T}"/> first inside the command's transaction: <c>null</c> means the key
/// is claimed (or none was sent) and the command runs; otherwise roll back and return the replay. After the save, call
/// <see cref="CompleteAsync{T}"/> in the same transaction.
/// </summary>
internal sealed partial class IdempotentRequests(IdempotencyStore store, IOptions<JsonOptions> json, ILogger<IdempotentRequests> logger)
{
    public async Task<Result<T>?> TryReplayAsync<T>(VenueId venueId, string operation, string? key, object request, CancellationToken cancellationToken)
    {
        if (key is null)
        {
            return null;
        }

        var hash = Hash(request);
        var earlier = await store.TryClaimAsync(venueId, operation, key, hash, cancellationToken);
        if (earlier is null)
        {
            return null;
        }

        if (!string.Equals(earlier.RequestHash, hash, StringComparison.Ordinal))
        {
            return Error.Rule(ProblemCodes.IdempotencyKeyReused);
        }

        LogReplayed(logger, operation);
        return JsonSerializer.Deserialize<T>(earlier.Response, json.Value.SerializerOptions)!;
    }

    public Task CompleteAsync<T>(VenueId venueId, string operation, string? key, T response, CancellationToken cancellationToken) =>
        key is null
            ? Task.CompletedTask
            : store.CompleteAsync(
                venueId, operation, key, StatusCodes.Status201Created,
                JsonSerializer.Serialize(response, json.Value.SerializerOptions), cancellationToken);

    private string Hash(object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, request.GetType(), json.Value.SerializerOptions))));

    [LoggerMessage(Level = LogLevel.Information, Message = "Idempotent replay of {Operation}")]
    private static partial void LogReplayed(ILogger logger, string operation);
}
