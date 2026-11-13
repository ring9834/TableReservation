using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// The messages of one venue that were created or changed status in a committed transaction. <see cref="TableReadyEntryIds"/>
/// are the waitlist entries of the TableReady messages among them. IDs only, never bodies or numbers.
/// </summary>
public sealed record MessageChanges(VenueId VenueId, IReadOnlyList<Guid> MessageIds, IReadOnlyList<Guid> TableReadyEntryIds);

/// <summary>
/// Tells the venue's screens that messages changed (Design §7.1 Visibility, §15): called by
/// <see cref="MessageChangeInterceptor"/> only after the transaction commits. The API pushes <c>messageLogChanged</c>
/// (and <c>waitlistChanged</c> for TableReady); without the API nothing is pushed.
/// </summary>
public interface IMessageChangeNotifier
{
    Task NotifyAsync(MessageChanges changes, CancellationToken cancellationToken);
}

/// <summary>Pushes nothing (workers hosted without the API, and tests that don't listen).</summary>
public sealed class NullMessageChangeNotifier : IMessageChangeNotifier
{
    public Task NotifyAsync(MessageChanges changes, CancellationToken cancellationToken) => Task.CompletedTask;
}
