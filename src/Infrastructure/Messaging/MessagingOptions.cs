namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// Settings for message delivery (configuration section <c>Messaging</c>, Design §7.1, §7.2). The worker runs only when
/// <c>Outbox:RunWorkers</c> is on, like the other workers.
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>How long the worker waits for a signal before polling anyway (infrastructure clock).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows claimed per transaction.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How long after Sent the demo phone panel reports Delivered (AC4.4).</summary>
    public TimeSpan PhonePanelDeliveryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsValid => PollInterval > TimeSpan.Zero && BatchSize > 0 && PhonePanelDeliveryDelay >= TimeSpan.Zero;
}
