using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// Tells the rest of the system about message changes only once they are committed (Design §7.1, §7.2). Scoped: one
/// instance per <c>DbContext</c>, next to the outbox interceptor. The commit, rollback and savepoint bookkeeping is
/// <see cref="CommittedChangesInterceptor{TChange}"/>'s.
/// <list type="bullet">
/// <item>Notes every <see cref="Message"/> that is added or whose status or attempts change.</item>
/// <item>Committed: rings <see cref="MessageSignal"/> when a message was queued, and hands each venue's changes to
/// <see cref="IMessageChangeNotifier"/> (<c>messageLogChanged</c>). A failed push is logged and swallowed.</item>
/// </list>
/// Logs IDs only.
/// </summary>
public sealed partial class MessageChangeInterceptor(
    MessageSignal signal,
    IMessageChangeNotifier notifier,
    ILogger<MessageChangeInterceptor> logger)
    : CommittedChangesInterceptor<MessageChangeInterceptor.Change>
{
    protected override IEnumerable<Change> Collect(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<Message>())
        {
            var added = entry.State == EntityState.Added;
            var changed = entry.State == EntityState.Modified
                && (entry.Property(m => m.Status).IsModified || entry.Property(m => m.Attempts).IsModified);
            if (added || changed)
            {
                var m = entry.Entity;
                yield return new Change(m.VenueId, m.Id.Value, m.Purpose, m.SubjectId, Queued: added && m.Status == MessageStatus.Queued);
            }
        }
    }

    protected override void OnRolledBackToSavepoint(int count) => LogRolledBack(logger, count);

    protected override async Task PublishAsync(IReadOnlyList<Change> changes)
    {
        if (changes.Any(c => c.Queued))
        {
            signal.Notify();
        }

        foreach (var venue in changes.GroupBy(c => c.VenueId))
        {
            var messageIds = venue.Select(c => c.MessageId).Distinct().ToList();
            var entryIds = venue.Where(c => c.Purpose == MessagePurpose.TableReady).Select(c => c.SubjectId).Distinct().ToList();
            try
            {
                await notifier.NotifyAsync(new MessageChanges(venue.Key, messageIds, entryIds), CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The exception itself is not attached: its message could echo a payload.
                LogNotifyFailed(logger, ex.GetType().Name, venue.Key.Value, messageIds.Count);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Messages: {Count} message changes rolled back with their savepoint; nothing is pushed for them")]
    private static partial void LogRolledBack(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messages: {ExceptionType} while pushing {Count} message changes for venue {VenueId}; screens refetch on reconnect")]
    private static partial void LogNotifyFailed(ILogger logger, string exceptionType, Guid venueId, int count);

    /// <summary>One changed message: IDs only.</summary>
    public sealed record Change(VenueId VenueId, Guid MessageId, MessagePurpose Purpose, Guid SubjectId, bool Queued);
}
