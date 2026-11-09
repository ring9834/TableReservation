namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// One row of the <c>inbox</c> table: this consumer has processed this message (Design §5.4, §5.5).
/// Consumers are event handlers or webhooks (<c>stripe-webhook</c>, <c>pos-webhook</c>).
/// </summary>
public sealed class InboxEntry(string consumer, string messageId, DateTimeOffset processedAt)
{
    public string Consumer { get; private set; } = consumer;

    public string MessageId { get; private set; } = messageId;

    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
