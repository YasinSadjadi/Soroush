namespace Soroush.Persistence.Abstractions;

/// <summary>Observes transaction lifecycle operations in registration order.</summary>
/// <remarks>After-hook failures do not reverse completed provider operations and do not invoke failed hooks.
/// Failure hooks receive an uncanceled cleanup token. Disposal rollback executes rollback hooks;
/// if a before-rollback hook fails, provider rollback is still attempted to release the transaction.</remarks>
public interface ITransactionInterceptor
{
    /// <summary>Runs before the provider starts a transaction.</summary>
    Task TransactionStartingAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Runs after the transaction has started.</summary>
    Task TransactionStartedAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Runs before the provider commits.</summary>
    Task TransactionCommittingAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Runs after commit has succeeded. Failures cannot undo the commit.</summary>
    Task TransactionCommittedAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Runs before the provider rolls back.</summary>
    Task TransactionRollingBackAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Runs after rollback has succeeded.</summary>
    Task TransactionRolledBackAsync(TransactionContext context, CancellationToken cancellationToken);
    /// <summary>Observes a before-hook or provider failure for the operation identified by the context.</summary>
    Task TransactionFailedAsync(TransactionContext context, Exception exception, CancellationToken cancellationToken);
}
