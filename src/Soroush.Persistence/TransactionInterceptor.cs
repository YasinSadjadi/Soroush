using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>A transaction interceptor with no-op defaults for optional lifecycle hooks.</summary>
public abstract class TransactionInterceptor : ITransactionInterceptor
{
    /// <inheritdoc />
    public virtual Task TransactionStartingAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionStartedAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionCommittingAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionCommittedAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionRollingBackAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionRolledBackAsync(TransactionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc />
    public virtual Task TransactionFailedAsync(TransactionContext context, Exception exception, CancellationToken cancellationToken) => Task.CompletedTask;
}
