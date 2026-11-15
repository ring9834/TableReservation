using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// The POS webhook signature (Design §6.7, docs/api/pos-messages.md): header <c>Pos-Signature: t={unix seconds},v1={hex
/// HMAC-SHA256(secret, "{t}.{body}")}</c>, accepted within <see cref="PosOptions.SignatureTolerance"/> either side of
/// now (infrastructure clock). The same scheme Stripe uses, so a replayed or edited body is refused.
/// </summary>
public static class PosWebhookSigner
{
    public const string Header = "Pos-Signature";

    public static string Sign(string body, string secret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={timestamp},v1={Convert.ToHexStringLower(Mac(secret, timestamp, body))}";
    }

    /// <summary>True when <paramref name="header"/> has a timestamp within the tolerance and a matching <c>v1</c>.</summary>
    public static bool Verify(string body, string? header, string? secret, DateTimeOffset now, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            switch (pair[0].Trim())
            {
                case "t":
                    timestamp = pair[1].Trim();
                    break;
                case "v1":
                    signatures.Add(pair[1].Trim());
                    break;
            }
        }

        if (timestamp is null || signatures.Count == 0
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
        {
            return false;
        }

        var expected = Mac(secret, timestamp, body);
        foreach (var signature in signatures)
        {
            byte[] given;
            try
            {
                given = Convert.FromHexString(signature);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(given, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Mac(string secret, string timestamp, string body) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
}
