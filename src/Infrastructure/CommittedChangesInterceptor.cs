using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Reservation.Infrastructure;

/// <summary>
/// Acts on changes only once they are committed (Design §7.2, §6.7). Scoped: one instance per <c>DbContext</c>.
/// <list type="bullet">
/// <item>Saving: <see cref="Collect"/> notes the changes this save writes.</item>
/// <item>Saved: keeps those notes until the surrounding transaction commits (or publishes at once without one).</item>
/// <item>Committed: hands everything noted to <see cref="PublishAsync"/>.</item>
/// <item>Rolled back, or the save failed: forgets what it noted.</item>
/// <item>Savepoints: each outbox handler runs in its own savepoint (§5.4). A rollback to a savepoint forgets what was
/// noted since it was created, so a handler that failed after saving publishes nothing, while the other handlers' changes
/// in the same transaction still do. A release keeps them. EF Core's own savepoints around a save inside a transaction
/// go through the same stack.</item>
/// </list>
/// </summary>
/// <typeparam name="TChange">What one changed row contributes; IDs only.</typeparam>
public abstract class CommittedChangesInterceptor<TChange> : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly List<TChange> _saving = [];
    private readonly List<TChange> _committing = [];
    private readonly Stack<int> _savepoints = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Note(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (Saved(eventData.Context))
        {
            PublishCommittedAsync().GetAwaiter().GetResult();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (Saved(eventData.Context))
        {
            await PublishCommittedAsync();
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
        PublishCommittedAsync().GetAwaiter().GetResult();

    Task IDbTransactionInterceptor.TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken) =>
        PublishCommittedAsync();

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

    /// <summary>The changes the save about to run will write.</summary>
    protected abstract IEnumerable<TChange> Collect(DbContext context);

    /// <summary>Called once per commit with everything committed; must not throw for a failed push (log and swallow).</summary>
    protected abstract Task PublishAsync(IReadOnlyList<TChange> changes);

    /// <summary>Called when a savepoint rollback dropped <paramref name="count"/> noted changes.</summary>
    protected virtual void OnRolledBackToSavepoint(int count)
    {
    }

    private void Note(DbContext? context)
    {
        _saving.Clear();
        if (context is not null)
        {
            _saving.AddRange(Collect(context));
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
            OnRolledBackToSavepoint(_committing.Count - mark);
            _committing.RemoveRange(mark, _committing.Count - mark);
        }
    }

    private void Forget()
    {
        _saving.Clear();
        _committing.Clear();
        _savepoints.Clear();
    }

    private Task PublishCommittedAsync()
    {
        _savepoints.Clear();
        if (_committing.Count == 0)
        {
            return Task.CompletedTask;
        }

        var changes = _committing.ToList();
        _committing.Clear();
        return PublishAsync(changes);
    }
}
