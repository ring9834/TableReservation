using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reservation.Api.Pipeline;

/// <summary>
/// Works out the rate-limit key of a guest-link request (Design §18.10) before the rate limiter runs: a SHA-256 of the
/// token, so each link has its own bucket. The token comes from the query string (<c>GET /guest/bookings?token</c>) or
/// the small JSON body (<c>POST …/confirm</c>, <c>…/cancel</c> <c>{token}</c>), which is buffered and rewound so the
/// endpoint still binds it. Only the hash is kept, in <see cref="HttpContext.Items"/>; the token is never a key, never
/// stored and never logged. A body that is too large, not JSON or without a token gets no key, and the request counts in
/// the per-IP bucket instead. Runs after routing (it needs the endpoint) and before <c>UseRateLimiter</c>.
/// </summary>
internal sealed class GuestLinkRateKeyMiddleware(RequestDelegate next)
{
    /// <summary>A guest-link body is <c>{"token":"…"}</c>, under 300 bytes; anything larger isn't read.</summary>
    public const int MaxBodyBytes = 4 * 1024;

    private static readonly object _itemKey = new();

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AnonymousBucketMetadata>() is { Bucket: AnonymousBucket.GuestLink })
        {
            var token = HttpMethods.IsGet(context.Request.Method)
                ? context.Request.Query["token"].ToString()
                : await ReadBodyTokenAsync(context.Request, context.RequestAborted);
            if (!string.IsNullOrEmpty(token))
            {
                context.Items[_itemKey] = Hash(token);
            }
        }

        await next(context);
    }

    /// <summary>The link's key for the rate limiter, or <c>null</c> when the request has no readable token.</summary>
    public static string? KeyOf(HttpContext context) => context.Items.TryGetValue(_itemKey, out var key) ? key as string : null;

    /// <summary>The first 128 bits of SHA-256(token), hex. Not reversible; the same link always gives the same key.</summary>
    internal static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 16);

    private static async Task<string?> ReadBodyTokenAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        request.EnableBuffering(MaxBodyBytes);
        try
        {
            var buffer = new byte[MaxBodyBytes + 1];
            var read = 0;
            int count;
            while (read < buffer.Length && (count = await request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += count;
            }

            if (read == 0 || read > MaxBodyBytes)
            {
                return null;
            }

            using var json = JsonDocument.Parse(buffer.AsMemory(0, read));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "token", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null; // the endpoint answers the bad body as usual
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
