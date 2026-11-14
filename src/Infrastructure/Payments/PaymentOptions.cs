namespace Reservation.Infrastructure.Payments;

/// <summary>Which payment provider takes deposits and refunds (M8a, Design §8.4).</summary>
public enum PaymentProviderKind
{
    /// <summary>No provider configured: start-up fails with a message naming <c>Payments:Provider</c>.</summary>
    None,

    /// <summary>Stripe (test mode in the demo). Needs <c>Stripe:SecretKey</c> and <c>Stripe:WebhookSecret</c>.</summary>
    Stripe,

    /// <summary>A deterministic in-process stand-in: Development, integration tests and CI. Calls nothing.</summary>
    Fake,
}

/// <summary>Configuration section <c>Payments</c>.</summary>
public sealed class PaymentOptions
{
    public const string Section = "Payments";

    public PaymentProviderKind Provider { get; set; }
}

/// <summary>
/// Configuration section <c>Stripe</c>: .NET user secrets locally, Secrets Manager in AWS. Never logged. The webhook
/// secret is also used with the Fake provider when set, so tests verify real Stripe signatures.
/// </summary>
public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>Secret API key (<c>sk_test_…</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>Webhook signing secret (<c>whsec_…</c>).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>How old a signed webhook may be (Stripe's default).</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Configuration section <c>Payments:HoldExpiry</c>.</summary>
public sealed class PaymentHoldExpiryOptions
{
    public const string Section = "Payments:HoldExpiry";

    /// <summary>How often <see cref="PaymentHoldExpiryJob"/> looks for expired holds (infrastructure clock).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}
