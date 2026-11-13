using Reservation.Infrastructure.Messaging;

namespace Reservation.Api.Realtime;

/// <summary>
/// Pushes message changes to the venue's screens once they are committed (Design §7.1 Visibility, §7.2, §15):
/// <c>messageLogChanged {messageIds}</c> for the presenter's phone panel and, for TableReady messages,
/// <c>waitlistChanged {entryIds}</c>, so host screens refresh the entry's sent / delivered / failed status (AC4.4).
/// IDs only. Called by <c>MessageChangeInterceptor</c>, which logs and swallows a failed push.
/// </summary>
internal sealed class MessageChangePusher(IVenueBroadcaster broadcaster) : IMessageChangeNotifier
{
    public async Task NotifyAsync(MessageChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await broadcaster.SendAsync(
            changes.VenueId,
            new RealtimeNotice(NoticeMethods.MessageLogChanged, new MessageLogChangedNotice(changes.MessageIds)),
            cancellationToken);
        if (changes.TableReadyEntryIds.Count > 0)
        {
            await broadcaster.SendAsync(
                changes.VenueId,
                new RealtimeNotice(NoticeMethods.WaitlistChanged, new WaitlistChangedNotice(changes.TableReadyEntryIds, null, null)),
                cancellationToken);
        }
    }
}
