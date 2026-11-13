using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// What the worker hands a provider. <see cref="ClientReference"/> is the message ID, passed as the provider's idempotency
/// or client reference, so a retry after a timeout or a crash does not send twice (Design §7.1).
/// </summary>
public sealed record OutgoingMessage(MessageId MessageId, MessageChannel Channel, string To, string Body)
{
    public string ClientReference => MessageId.Value.ToString();
}

public enum SendOutcome
{
    /// <summary>The provider accepted the message.</summary>
    Accepted,

    /// <summary>A temporary error (timeout, throttling): retried with backoff until the message expires.</summary>
    Temporary,

    /// <summary>A permanent error (invalid or unreachable number, opted out): Failed at once, no retry.</summary>
    Permanent,
}

/// <summary>
/// A provider's answer. <paramref name="ProviderRef"/> is set when accepted; <paramref name="Error"/> is a short code when
/// not, never a provider's own text, which could echo the number. <paramref name="DeliveredAfter"/> is set by a sender that
/// reports delivery itself after a delay (the demo phone panel); providers with delivery webhooks leave it <c>null</c>.
/// </summary>
public sealed record SendResult(SendOutcome Outcome, string? ProviderRef, string? Error, TimeSpan? DeliveredAfter)
{
    public static SendResult Accepted(string providerRef, TimeSpan? deliveredAfter = null) =>
        new(SendOutcome.Accepted, providerRef, null, deliveredAfter);

    public static SendResult Temporary(string error) => new(SendOutcome.Temporary, null, error, null);

    public static SendResult Permanent(string error) => new(SendOutcome.Permanent, null, error, null);
}

/// <summary>Sends one message through a provider (Design §7). Demo: <see cref="PhonePanelSender"/>; pilot: SMS and email.</summary>
public interface IMessageSender
{
    Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}
