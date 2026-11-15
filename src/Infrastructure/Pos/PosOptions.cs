namespace Reservation.Infrastructure.Pos;

/// <summary>
/// Configuration section <c>Pos</c> (Design §6.4, §6.7). <see cref="WebhookSecret"/> comes from .NET user secrets locally
/// and Secrets Manager in AWS; it is never logged. Without it every POS webhook is refused.
/// </summary>
public sealed class PosOptions
{
    public const string Section = "Pos";

    /// <summary>Signs and verifies <c>BillClosed</c> webhooks (HMAC-SHA256). Required when <c>Demo:Enabled</c>.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>How old a signed webhook may be (infrastructure clock).</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the delivery worker looks for due messages when nothing wakes it.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Retry interval while the POS is unavailable (§6.4: 5 s in the demo).</summary>
    public TimeSpan UnavailableRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a message may wait for an unavailable POS before it fails.</summary>
    public TimeSpan UnavailableWindow { get; set; } = TimeSpan.FromHours(24);

    public bool IsValid =>
        SignatureTolerance > TimeSpan.Zero && PollInterval > TimeSpan.Zero && UnavailableRetry > TimeSpan.Zero && UnavailableWindow > TimeSpan.Zero;
}
