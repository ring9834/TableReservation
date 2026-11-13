using Microsoft.Extensions.Options;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// The demo's sender (Design §7, §17): no real SMS is sent. The <c>messages</c> row is what the presenter's phone panel
/// shows, so accepting is all there is to do, and repeating a send is harmless. It reports delivery after
/// <see cref="MessagingOptions.PhonePanelDeliveryDelay"/>; the worker marks the row Delivered once that is due, so it
/// survives a restart (no fire-and-forget delay).
/// </summary>
public sealed class PhonePanelSender(IOptions<MessagingOptions> options) : IMessageSender
{
    public const string ReferencePrefix = "panel-";

    public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Task.FromResult(SendResult.Accepted(ReferencePrefix + message.ClientReference, options.Value.PhonePanelDeliveryDelay));
    }
}
