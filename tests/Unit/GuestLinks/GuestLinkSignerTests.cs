using System.Buffers.Text;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;
using Reservation.Api.GuestLinks;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.GuestLinks;

/// <summary><see cref="GuestLinkSigner"/> (Design §7 "About GuestLinkSigner", §7.3; ADR-004).</summary>
public class GuestLinkSignerTests
{
    private const string Key = "unit-test-guest-link-key-0000000000";
    private const string OtherKey = "another-unit-test-key-1111111111111";

    private static readonly BookingId _booking = new(Guid.Parse("0193a7d0-1111-7000-8000-000000000011"));
    private static readonly DateTimeOffset _start = TestData.Start;

    private readonly TestClock _clock = new(TestData.Start.AddHours(-2));

    [Theory]
    [InlineData(1)] // Manage
    [InlineData(2)] // Confirm
    [InlineData(3)] // Cancel
    public void A_token_round_trips_with_its_booking_action_expiry_and_key(int value)
    {
        var action = (LinkAction)value;
        var signer = Signer();

        var link = signer.Verify(signer.Create(_booking, action, _start));

        Assert.True(link.IsSuccess);
        Assert.Equal(new GuestLink(_booking, action, _start, "1"), link.Value);
    }

    [Fact]
    public void A_token_is_url_safe_and_short()
    {
        var token = Signer().Create(_booking, LinkAction.Confirm, _start);

        Assert.Matches("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", token);
        Assert.InRange(token.Length, 40, 100);
    }

    [Fact]
    public void A_changed_payload_is_invalid()
    {
        var signer = Signer();
        var token = signer.Create(_booking, LinkAction.Confirm, _start);
        var (payload, mac) = Split(token);

        // Turn the Confirm link into a Cancel link without re-signing.
        var bytes = Base64Url.DecodeFromChars(payload);
        bytes[^9] = (byte)LinkAction.Cancel;
        var tampered = Base64Url.EncodeToString(bytes) + "." + mac;

        AssertInvalid(signer.Verify(tampered));
    }

    [Fact]
    public void A_later_expiry_without_the_key_is_invalid()
    {
        var signer = Signer();
        var (payload, mac) = Split(signer.Create(_booking, LinkAction.Cancel, _start));
        var bytes = Base64Url.DecodeFromChars(payload);
        bytes[^1] ^= 0x01;

        AssertInvalid(signer.Verify(Base64Url.EncodeToString(bytes) + "." + mac));
    }

    [Fact]
    public void A_changed_signature_is_invalid()
    {
        var signer = Signer();
        var (payload, mac) = Split(signer.Create(_booking, LinkAction.Confirm, _start));
        var bytes = Base64Url.DecodeFromChars(mac);
        bytes[0] ^= 0xFF;

        AssertInvalid(signer.Verify(payload + "." + Base64Url.EncodeToString(bytes)));
    }

    [Fact]
    public void A_token_signed_with_another_key_is_invalid()
    {
        var token = Signer(OtherKey).Create(_booking, LinkAction.Confirm, _start);

        AssertInvalid(Signer().Verify(token));
    }

    [Fact]
    public void After_rotation_a_link_signed_with_the_previous_key_still_works()
    {
        var old = Signer(Key, keyId: "1").Create(_booking, LinkAction.Cancel, _start);
        var rotated = Signer(OtherKey, keyId: "2", previous: new() { ["1"] = Key });

        var link = rotated.Verify(old);

        Assert.True(link.IsSuccess);
        Assert.Equal("1", link.Value.KeyId);
        Assert.Equal("2", rotated.Verify(rotated.Create(_booking, LinkAction.Cancel, _start)).Value.KeyId);
    }

    [Fact]
    public void A_link_with_a_key_id_no_longer_configured_is_invalid()
    {
        var old = Signer(Key, keyId: "1").Create(_booking, LinkAction.Cancel, _start);

        AssertInvalid(Signer(OtherKey, keyId: "2").Verify(old));
    }

    [Fact]
    public void A_link_is_expired_from_its_expiry_on_the_business_clock()
    {
        var signer = Signer();
        var token = signer.Create(_booking, LinkAction.Confirm, _start);

        _clock.Set(_start.AddSeconds(-1));
        Assert.True(signer.Verify(token).IsSuccess);

        _clock.Set(_start);
        var expired = signer.Verify(token);
        Assert.True(expired.IsFailure);
        Assert.Equal(ProblemCodes.LinkExpired, expired.Error!.Code);
        Assert.Equal(ErrorKind.Gone, expired.Error.Kind);
    }

    [Fact]
    public void A_forged_link_past_its_expiry_is_invalid_not_expired()
    {
        var token = Signer(OtherKey).Create(_booking, LinkAction.Confirm, _start);
        _clock.Set(_start.AddDays(1));

        AssertInvalid(Signer().Verify(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData(".")]
    [InlineData("!!!!.????")]
    [InlineData("AQ.AQ")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]
    public void Garbage_is_invalid_and_never_throws(string? token) => AssertInvalid(Signer().Verify(token));

    [Fact]
    public void A_very_long_token_is_invalid() => AssertInvalid(Signer().Verify(new string('A', 5_000) + ".AA"));

    [Fact]
    public void Links_point_at_the_manage_page_with_the_token_in_the_query_string()
    {
        var options = Options(Key, "1", []);
        var urls = new GuestLinkUrls(options, new GuestLinkSigner(options, _clock));

        var url = urls.For(_booking, LinkAction.Manage, _start);

        Assert.StartsWith("https://guest.test/manage/?token=", url, StringComparison.Ordinal);
        Assert.True(Signer().Verify(url["https://guest.test/manage/?token=".Length..]).IsSuccess);
    }

    [Theory]
    [InlineData("short", "1", "https://guest.test", false)]
    [InlineData(Key, "1", "https://guest.test", true)]
    [InlineData(Key, "", "https://guest.test", false)]
    [InlineData(Key, "key id", "https://guest.test", false)]
    [InlineData(Key, "1", "not a url", false)]
    [InlineData(Key, "1", "ftp://guest.test", false)]
    [InlineData(Key, "1", "https://guest.test/?token=x", false)]
    public void Options_are_checked(string key, string keyId, string baseUrl, bool valid)
    {
        var options = new GuestLinkOptions { SigningKey = key, KeyId = keyId, BaseUrl = baseUrl };

        Assert.Equal(valid, options.HasValidKeys && options.HasValidKeyId && options.HasValidBaseUrl);
    }

    [Fact]
    public void A_short_previous_key_or_one_reusing_the_current_id_is_refused()
    {
        Assert.False(new GuestLinkOptions { SigningKey = Key, PreviousKeys = new() { ["0"] = "short" } }.HasValidKeys);
        Assert.False(new GuestLinkOptions { SigningKey = Key, KeyId = "1", PreviousKeys = new() { ["1"] = OtherKey } }.HasValidKeys);
    }

    private static void AssertInvalid(Result<GuestLink> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ProblemCodes.InvalidLink, result.Error!.Code);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Null(result.Error.Fields);
    }

    private static (string Payload, string Mac) Split(string token)
    {
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        return (token[..dot], token[(dot + 1)..]);
    }

    private static IOptions<GuestLinkOptions> Options(string key, string keyId, Dictionary<string, string> previous) =>
        Microsoft.Extensions.Options.Options.Create(new GuestLinkOptions
        {
            SigningKey = key, KeyId = keyId, PreviousKeys = previous, BaseUrl = "https://guest.test",
        });

    private GuestLinkSigner Signer(string key = Key, string keyId = "1", Dictionary<string, string>? previous = null) =>
        new(Options(key, keyId, previous ?? []), _clock);
}
