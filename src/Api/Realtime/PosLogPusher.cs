using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Realtime;

/// <summary>
/// Pushes POS log changes to the venue's screens once they are committed (Design §6.2, §6.7, §15):
/// <c>posLogChanged {posMessageIds}</c>. IDs only. Called by <c>PosMessageChangeInterceptor</c>, which logs and swallows a
/// failed push.
/// </summary>
internal sealed class PosLogPusher(IVenueBroadcaster broadcaster) : IPosLogNotifier
{
    public Task NotifyAsync(PosLogChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return broadcaster.SendAsync(
            changes.VenueId,
            new RealtimeNotice(NoticeMethods.PosLogChanged, new PosLogChangedNotice(changes.PosMessageIds)),
            cancellationToken);
    }
}
