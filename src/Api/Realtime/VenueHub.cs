using Microsoft.AspNetCore.SignalR;
using Reservation.Api.Auth;
using Reservation.Domain.Common;

namespace Reservation.Api.Realtime;

/// <summary>
/// <c>/hubs/venue</c> (Design §15, §15.1): host screens receive notices here. The <c>Host</c> policy and the demo
/// session cookie guard the connection; on connect the server adds it to <c>venue:{id}</c> for each
/// <c>venue_ids</c> claim, so the client never names a venue. The hub has no client-callable methods.
/// </summary>
internal sealed class VenueHub : Hub
{
    public const string Path = "/hubs/venue";

    public override async Task OnConnectedAsync()
    {
        foreach (var venueId in DemoSession.VenueIds(Context.User))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, VenueGroups.For(venueId), Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }
}

internal static class VenueGroups
{
    public static string For(VenueId venueId) => $"venue:{venueId.Value}";
}

/// <summary>Sends a notice to one venue's screens. A seam so tests can make the push fail.</summary>
internal interface IVenueBroadcaster
{
    Task SendAsync(VenueId venueId, RealtimeNotice notice, CancellationToken cancellationToken);
}

internal sealed class HubVenueBroadcaster(IHubContext<VenueHub> hub) : IVenueBroadcaster
{
    public Task SendAsync(VenueId venueId, RealtimeNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return hub.Clients.Group(VenueGroups.For(venueId)).SendAsync(notice.Method, notice.Payload, cancellationToken);
    }
}
