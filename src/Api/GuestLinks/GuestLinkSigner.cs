using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;
using Reservation.Domain.Common;

namespace Reservation.Api.GuestLinks;

/// <summary>What a guest link lets the guest do (Design §7, §7.3). Also the kind of a message action.</summary>
internal enum LinkAction
{
    /// <summary>The confirmation's manage-booking link: view the booking and cancel it, never confirm.</summary>
    Manage = 1,

    /// <summary>The reminder's Confirm link.</summary>
    Confirm = 2,

    /// <summary>The reminder's Cancel link.</summary>
    Cancel = 3,
}

/// <summary>A verified guest link.</summary>
internal sealed record GuestLink(BookingId BookingId, LinkAction Action, DateTimeOffset Expires, string KeyId);

/// <summary>
/// Section <c>GuestLinks</c> (Design §7.3). <see cref="SigningKey"/> comes from .NET user secrets locally and Secrets
/// Manager in AWS; <see cref="PreviousKeys"/> keeps links signed with an older key working until they expire.
/// </summary>
internal sealed class GuestLinkOptions
{
    public const string Section = "GuestLinks";

    public const int MinimumKeyLength = 32;

    /// <summary>The key new links are signed with.</summary>
    public string? SigningKey { get; set; }

    /// <summary>The ID stamped into new links, so the key can be rotated (default <c>1</c>).</summary>
    public string KeyId { get; set; } = "1";

    /// <summary>Older keys by key ID, still accepted when verifying.</summary>
    public Dictionary<string, string> PreviousKeys { get; set; } = [];

    /// <summary>The guest web app's origin, e.g. <c>https://localhost:3000</c>; links are <c>{BaseUrl}/manage/?token=…</c>.</summary>
    public string? BaseUrl { get; set; }

    internal bool HasValidKeys =>
        SigningKey is { Length: >= MinimumKeyLength }
        && PreviousKeys.Values.All(k => k is { Length: >= MinimumKeyLength })
        && !PreviousKeys.ContainsKey(KeyId);

    internal bool HasValidKeyId => KeyId is { Length: >= 1 and <= 16 } && KeyId.All(char.IsAsciiLetterOrDigit);

    internal bool HasValidBaseUrl =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query.TrimStart('?'));
}

/// <summary>
/// Creates and checks the signed links that let a guest act on a booking without an account (Design §7 "About
/// GuestLinkSigner", §7.3). A token is <c>base64url(payload) "." base64url(HMAC-SHA256(key, payload))</c>, with a
/// compact binary payload so the texts stay short: version, key ID, booking ID, action and expiry (Unix seconds).
/// Anyone can read a token; no one can change or make one without the key. <see cref="Verify"/> never throws and never
/// logs: a malformed, forged or unknown-key token is <c>invalid_link</c>, a genuine one past its expiry
/// <c>link_expired</c>. The expiry is the booking start, a business time, so it is compared on the business clock
/// (CLAUDE.md rule 14): with the demo clock fixed at 18:25 a 20:30 booking's link stays valid.
/// </summary>
internal sealed class GuestLinkSigner
{
    private const byte FormatVersion = 1;
    private const int MacLength = 32;
    private const int MaxTokenLength = 256;

    private readonly TimeProvider _clock;
    private readonly string _keyId;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public GuestLinkSigner(IOptions<GuestLinkOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var value = options.Value;
        _clock = clock;
        _keyId = value.KeyId;
        _keys[value.KeyId] = Encoding.UTF8.GetBytes(value.SigningKey
            ?? throw new InvalidOperationException("GuestLinks:SigningKey is not set."));
        foreach (var (keyId, key) in value.PreviousKeys)
        {
            _keys[keyId] = Encoding.UTF8.GetBytes(key);
        }
    }

    public static Error InvalidLink { get; } = new(ProblemCodes.InvalidLink, "errors.invalid_link", ErrorKind.Validation);

    public static Error LinkExpired { get; } = new(ProblemCodes.LinkExpired, "errors.link_expired", ErrorKind.Gone);

    /// <summary>A URL-safe token for the booking and action, valid until <paramref name="expires"/> (normally the booking start).</summary>
    public string Create(BookingId bookingId, LinkAction action, DateTimeOffset expires)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        var keyId = Encoding.ASCII.GetBytes(_keyId);
        var payload = new byte[1 + 1 + keyId.Length + 16 + 1 + 8];
        var span = payload.AsSpan();
        span[0] = FormatVersion;
        span[1] = (byte)keyId.Length;
        keyId.CopyTo(span[2..]);
        var at = 2 + keyId.Length;
        bookingId.Value.TryWriteBytes(span.Slice(at, 16), bigEndian: true, out _);
        span[at + 16] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(span[(at + 17)..], expires.ToUnixTimeSeconds());

        var mac = HMACSHA256.HashData(_keys[_keyId], payload);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    /// <summary>Checks the signature, then the expiry against business-clock now.</summary>
    public Result<GuestLink> Verify(string? token)
    {
        if (!TryParse(token, out var payload, out var mac)
            || !TryRead(payload, out var keyId, out var bookingId, out var action, out var expires)
            || !_keys.TryGetValue(keyId, out var key))
        {
            return InvalidLink;
        }

        var expected = HMACSHA256.HashData(key, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
        {
            return InvalidLink;
        }

        return expires <= _clock.GetUtcNow()
            ? LinkExpired
            : new GuestLink(bookingId, action, expires, keyId);
    }

    private static bool TryParse(string? token, [NotNullWhen(true)] out byte[]? payload, [NotNullWhen(true)] out byte[]? mac)
    {
        payload = null;
        mac = null;
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot != token.LastIndexOf('.') || !IsBase64Url(token))
        {
            return false;
        }

        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        return mac.Length == MacLength;
    }

    private static bool IsBase64Url(string token) =>
        token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool TryRead(
        byte[] payload, out string keyId, out BookingId bookingId, out LinkAction action, out DateTimeOffset expires)
    {
        keyId = string.Empty;
        bookingId = default;
        action = default;
        expires = default;
        if (payload.Length < 2 || payload[0] != FormatVersion)
        {
            return false;
        }

        var keyIdLength = payload[1];
        if (keyIdLength == 0 || payload.Length != 2 + keyIdLength + 16 + 1 + 8)
        {
            return false;
        }

        var span = payload.AsSpan();
        keyId = Encoding.ASCII.GetString(span.Slice(2, keyIdLength));
        var at = 2 + keyIdLength;
        bookingId = new BookingId(new Guid(span.Slice(at, 16), bigEndian: true));
        action = (LinkAction)span[at + 16];
        var seconds = BinaryPrimitives.ReadInt64BigEndian(span[(at + 17)..]);
        if (!Enum.IsDefined(action) || seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}

/// <summary>Builds the links that go into texts: <c>{BaseUrl}/manage/?token=…</c> (Design §7.3, §18.1). Never logged.</summary>
internal sealed class GuestLinkUrls(IOptions<GuestLinkOptions> options, GuestLinkSigner signer)
{
    private readonly string _base = (options.Value.BaseUrl ?? throw new InvalidOperationException("GuestLinks:BaseUrl is not set."))
        .TrimEnd('/');

    public string For(BookingId bookingId, LinkAction action, DateTimeOffset expires) =>
        $"{_base}/manage/?token={signer.Create(bookingId, action, expires)}";
}
