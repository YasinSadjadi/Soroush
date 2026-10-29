using System.Data;
using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>Runs application work with a transaction, save, commit, and failure cleanup.</summary>
public static class UnitOfWorkExtensions
{
    /// <summary>Begins a transaction, runs the operation once, saves, and commits. Never retries or joins an existing transaction.</summary>
    /// <remarks>The callback must not complete the transaction itself. Cancellation and failures trigger
    /// uncanceled rollback and disposal. After-commit hook failures propagate without attempting rollback.
    /// Cleanup failures are retained in the primary exception's Data under Soroush.Persistence.SecondaryExceptions.
    /// Rollback does not reset in-memory tracked entity state; discard the session after a failed operation.</remarks>
    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<TResult>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        if (unitOfWork is null) throw new ArgumentNullException(nameof(unitOfWork));
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        var transaction = await unitOfWork.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        TResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await operation(cancellationToken).ConfigureAwait(false);
            if (transaction.IsCompleted || !ReferenceEquals(unitOfWork.CurrentTransaction, transaction))
                throw new InvalidOperationException("The operation must not complete or replace its managed transaction.");
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!transaction.IsCompleted)
                await FailureCleanup.CaptureAsync(exception, () => transaction.RollbackAsync(CancellationToken.None)).ConfigureAwait(false);
            await FailureCleanup.CaptureAsync(exception, () => transaction.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }
        await transaction.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs an operation without a return value using the same transaction lifecycle as the generic overload.</summary>
    public static Task ExecuteInTransactionAsync(this IUnitOfWork unitOfWork,
        Func<CancellationToken, Task> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        return unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, isolationLevel, cancellationToken);
    }
}
