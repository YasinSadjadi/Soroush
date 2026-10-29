namespace Soroush.Persistence.Abstractions;

/// <summary>An explicit transaction owned and asynchronously disposed by its caller.</summary>
/// <remarks>Dispose must roll back an active, uncommitted transaction and release resources.
/// Successful commit and rollback are terminal operations; adapters must reject further operations.
/// A failed commit cannot be retried; rollback may still be attempted. Disposal is idempotent.</remarks>
public interface ITransaction : IAsyncDisposable
{
    /// <summary>Gets whether commit or rollback has completed successfully, even if subsequent adapter cleanup throws.
    /// A failed commit can have an unknown database outcome even when this is false.</summary>
    bool IsCompleted { get; }

    /// <summary>Commits changes already saved to the transaction. Does not implicitly save pending changes.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls back changes made within the transaction.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
