using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Infrastructure.Messaging;

/// <summary>
/// Tells the rest of the system about message changes only once they are committed (Design §7.1, §7.2). Scoped: one
/// instance per <c>DbContext</c>, next to the outbox interceptor.
/// <list type="bullet">
/// <item>Saving: notes every <see cref="Message"/> that is added or whose status or attempts change.</item>
/// <item>Saved: keeps those notes until the surrounding transaction commits (or acts at once without one).</item>
/// <item>Committed: rings <see cref="MessageSignal"/> when a message was queued, and hands each venue's changes to
/// <see cref="IMessageChangeNotifier"/> (<c>messageLogChanged</c>). A failed push is logged and swallowed.</item>
/// <item>Rolled back, or the save failed: forgets what it noted.</item>
/// <item>Savepoints: each outbox handler runs in its own savepoint (§5.4). A rollback to a savepoint forgets what was
/// noted since it was created, so a handler that failed after saving sends no signal and no notice, while the other
/// handlers' messages in the same transaction still do. A release keeps them. EF Core's own savepoints around a save
/// inside a transaction go through the same stack.</item>
/// </list>
/// Logs IDs only.
/// </summary>
public sealed partial class MessageChangeInterceptor(
    MessageSignal signal,
    IMessageChangeNotifier notifier,
    ILogger<MessageChangeInterceptor> logger)
    : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly List<Change> _saving = [];
    private readonly List<Change> _committing = [];
    private readonly Stack<int> _savepoints = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (Saved(eventData.Context))
        {
            PublishAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (Saved(eventData.Context))
        {
            await PublishAsync(CancellationToken.None);
        }

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _saving.Clear();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _saving.Clear();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => _saving.Clear();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        _saving.Clear();
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        PublishAsync(CancellationToken.None).GetAwaiter().GetResult();

    Task IDbTransactionInterceptor.TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken) =>
        PublishAsync(CancellationToken.None);

    void IDbTransactionInterceptor.TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget();

    Task IDbTransactionInterceptor.TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken)
    {
        Forget();
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) =>
        _savepoints.Push(_committing.Count);

    Task IDbTransactionInterceptor.CreatedSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        _savepoints.Push(_committing.Count);
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.ReleasedSavepoint(DbTransaction transaction, TransactionEventData eventData) => Release();

    Task IDbTransactionInterceptor.ReleasedSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        Release();
        return Task.CompletedTask;
    }

    void IDbTransactionInterceptor.RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => RollBackToSavepoint();

    Task IDbTransactionInterceptor.RolledBackToSavepointAsync(
        DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken)
    {
        RollBackToSavepoint();
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        _saving.Clear();
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<Message>())
        {
            var added = entry.State == EntityState.Added;
            var changed = entry.State == EntityState.Modified
                && (entry.Property(m => m.Status).IsModified || entry.Property(m => m.Attempts).IsModified);
            if (added || changed)
            {
                var m = entry.Entity;
                _saving.Add(new Change(m.VenueId, m.Id.Value, m.Purpose, m.SubjectId, Queued: added && m.Status == MessageStatus.Queued));
            }
        }
    }

    /// <summary>Moves what this save wrote to the commit list; true when there is no transaction, so it is committed now.</summary>
    private bool Saved(DbContext? context)
    {
        _committing.AddRange(_saving);
        _saving.Clear();
        return _committing.Count > 0 && context?.Database.CurrentTransaction is null;
    }

    private void Release()
    {
        if (_savepoints.Count > 0)
        {
            _savepoints.Pop();
        }
    }

    /// <summary>
    /// Undoes what was noted since the latest savepoint. The savepoint is popped too: the outbox handlers never return to
    /// a savepoint after rolling back to it, and the next handler creates its own.
    /// </summary>
    private void RollBackToSavepoint()
    {
        if (_savepoints.Count == 0)
        {
            return;
        }

        var mark = _savepoints.Pop();
        if (_committing.Count > mark)
        {
            LogRolledBack(logger, _committing.Count - mark);
            _committing.RemoveRange(mark, _committing.Count - mark);
        }
    }

    private void Forget()
    {
        _saving.Clear();
        _committing.Clear();
        _savepoints.Clear();
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        _savepoints.Clear();
        if (_committing.Count == 0)
        {
            return;
        }

        var changes = _committing.ToList();
        _committing.Clear();

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
                await notifier.NotifyAsync(new MessageChanges(venue.Key, messageIds, entryIds), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

    private sealed record Change(VenueId VenueId, Guid MessageId, MessagePurpose Purpose, Guid SubjectId, bool Queued);
}
