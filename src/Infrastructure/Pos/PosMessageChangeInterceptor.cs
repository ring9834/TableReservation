using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Pos;

/// <summary>The POS messages of one venue that were added or changed status or attempts in a committed transaction.</summary>
public sealed record PosLogChanges(VenueId VenueId, IReadOnlyList<Guid> PosMessageIds);

/// <summary>
/// Tells the venue's screens that the POS log changed (Design §6.2, §15): called only after the transaction commits.
/// The API pushes <c>posLogChanged</c>; without the API nothing is pushed.
/// </summary>
public interface IPosLogNotifier
{
    Task NotifyAsync(PosLogChanges changes, CancellationToken cancellationToken);
}

/// <summary>Pushes nothing (workers hosted without the API, and tests that don't listen).</summary>
public sealed class NullPosLogNotifier : IPosLogNotifier
{
    public Task NotifyAsync(PosLogChanges changes, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The POS twin of <c>MessageChangeInterceptor</c> (Design §6.7): notes every <see cref="PosMessage"/> that is added or
/// whose status or attempts change, and after the commit rings <see cref="PosSignal"/> (a new outbound message) and
/// hands each venue's IDs to <see cref="IPosLogNotifier"/>. A rollback, also to a handler's savepoint, sends nothing. An
/// unavailable retry changes neither status nor attempts, so it pushes nothing. Logs IDs only.
/// </summary>
public sealed partial class PosMessageChangeInterceptor(
    PosSignal signal,
    IPosLogNotifier notifier,
    ILogger<PosMessageChangeInterceptor> logger)
    : CommittedChangesInterceptor<PosMessageChangeInterceptor.Change>
{
    protected override IEnumerable<Change> Collect(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<PosMessage>())
        {
            var added = entry.State == EntityState.Added;
            var changed = entry.State == EntityState.Modified
                && (entry.Property(m => m.Status).IsModified || entry.Property(m => m.Attempts).IsModified);
            if (added || changed)
            {
                var m = entry.Entity;
                yield return new Change(m.VenueId, m.Id.Value, Queued: added && m.Direction == PosDirection.Outbound);
            }
        }
    }

    protected override async Task PublishAsync(IReadOnlyList<Change> changes)
    {
        if (changes.Any(c => c.Queued))
        {
            signal.Notify();
        }

        foreach (var venue in changes.GroupBy(c => c.VenueId))
        {
            var ids = venue.Select(c => c.PosMessageId).Distinct().ToList();
            try
            {
                await notifier.NotifyAsync(new PosLogChanges(venue.Key, ids), CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The exception itself is not attached: its message could echo a payload.
                LogNotifyFailed(logger, ex.GetType().Name, venue.Key.Value, ids.Count);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "POS: {ExceptionType} while pushing {Count} POS log changes for venue {VenueId}; screens refetch on reconnect")]
    private static partial void LogNotifyFailed(ILogger logger, string exceptionType, Guid venueId, int count);

    /// <summary>One changed POS message: IDs only.</summary>
    public sealed record Change(VenueId VenueId, Guid PosMessageId, bool Queued);
}
