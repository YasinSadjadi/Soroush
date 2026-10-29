using System.Data;
using System.Runtime.ExceptionServices;
using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>Coordinates a provider session, lifecycle interceptors, and owned transactions.</summary>
/// <remarks>The session is borrowed; its owner must dispose it after this coordinator.
/// Nested transactions and concurrent or reentrant operations are rejected. A provider persistence
/// failure faults the coordinator: rollback and disposal remain available, but further work requires a new scope.
/// Hook failures after save or commit do not undo the provider operation.</remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly IPersistenceSession _session;
    private readonly ISaveChangesInterceptor[] _saveInterceptors;
    private readonly ITransactionInterceptor[] _transactionInterceptors;
    private readonly List<CoordinatedTransaction> _transactions = new List<CoordinatedTransaction>();
    private CoordinatedTransaction? _currentTransaction;
    private int _busy;
    private bool _disposed;
    private bool _faulted;

    /// <summary>Creates a coordinator and snapshots interceptors in their registration order.</summary>
    public UnitOfWork(IPersistenceSession session,
        IEnumerable<ISaveChangesInterceptor>? saveInterceptors = null,
        IEnumerable<ITransactionInterceptor>? transactionInterceptors = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _saveInterceptors = Snapshot(saveInterceptors, nameof(saveInterceptors));
        _transactionInterceptors = Snapshot(transactionInterceptors, nameof(transactionInterceptors));
    }

    /// <inheritdoc />
    public ITransaction? CurrentTransaction => _currentTransaction?.IsCompleted == false ? _currentTransaction : null;

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            var context = new SaveChangesContext(this);
            int affectedEntries;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var interceptor in _saveInterceptors)
                    await interceptor.SavingChangesAsync(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try { affectedEntries = await _session.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
                catch { _faulted = true; throw; }
            }
            catch (Exception exception)
            {
                foreach (var interceptor in _saveInterceptors)
                    await FailureCleanup.CaptureAsync(exception, () => interceptor.SaveChangesFailedAsync(context, exception, CancellationToken.None)).ConfigureAwait(false);
                throw;
            }

            foreach (var interceptor in _saveInterceptors)
                await interceptor.SavedChangesAsync(context, affectedEntries, cancellationToken).ConfigureAwait(false);
            return affectedEntries;
        }
        finally { ExitOperation(); }
    }

    /// <inheritdoc />
    public async Task<ITransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            if (CurrentTransaction != null) throw new InvalidOperationException("Nested transactions are not supported.");
            var context = new TransactionContext(this, isolationLevel, TransactionOperation.Begin);
            ITransaction providerTransaction;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var interceptor in _transactionInterceptors)
                    await interceptor.TransactionStartingAsync(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    providerTransaction = await _session.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The provider returned no transaction.");
                }
                catch { _faulted = true; throw; }
            }
            catch (Exception exception)
            {
                await NotifyTransactionFailureAsync(context, exception).ConfigureAwait(false);
                throw;
            }

            var transaction = new CoordinatedTransaction(this, providerTransaction, isolationLevel);
            _transactions.Add(transaction);
            _currentTransaction = transaction;
            context = transaction.Context(TransactionOperation.Begin);
            try
            {
                foreach (var interceptor in _transactionInterceptors)
                    await interceptor.TransactionStartedAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The caller never received this handle, so all cleanup belongs to us.
                await FailureCleanup.CaptureAsync(exception, () => DisposeTransactionCoreAsync(transaction)).ConfigureAwait(false);
                throw;
            }
            return transaction;
        }
        finally { ExitOperation(); }
    }

    /// <summary>Rolls back active work and releases all transaction handles, including undisposed completed ones.
    /// Does not dispose the borrowed provider session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        EnterOperation(allowFaulted: true);
        try
        {
            _disposed = true;
            Exception? failure = null;
            foreach (var transaction in Enumerable.Reverse(_transactions.ToArray()))
            {
                if (failure != null)
                    await FailureCleanup.CaptureAsync(failure, () => DisposeTransactionCoreAsync(transaction)).ConfigureAwait(false);
                else
                {
                    try { await DisposeTransactionCoreAsync(transaction).ConfigureAwait(false); }
                    catch (Exception exception) { failure = exception; }
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { ExitOperation(); }
    }

    private async Task CommitAsync(CoordinatedTransaction transaction, CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            EnsureActive(transaction);
            var context = transaction.Context(TransactionOperation.Commit);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var interceptor in _transactionInterceptors)
                    await interceptor.TransactionCommittingAsync(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try { await transaction.Provider.CommitAsync(cancellationToken).ConfigureAwait(false); }
                catch { _faulted = true; throw; }
            }
            catch (Exception exception)
            {
                await NotifyTransactionFailureAsync(context, exception).ConfigureAwait(false);
                throw;
            }
            Complete(transaction);
            foreach (var interceptor in _transactionInterceptors)
                await interceptor.TransactionCommittedAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally { ExitOperation(); }
    }

    private async Task RollbackAsync(CoordinatedTransaction transaction, CancellationToken cancellationToken)
    {
        EnterOperation(allowFaulted: true);
        try
        {
            EnsureActive(transaction);
            await RollbackCoreAsync(transaction, cancellationToken, force: false).ConfigureAwait(false);
        }
        finally { ExitOperation(); }
    }

    private async Task RollbackCoreAsync(CoordinatedTransaction transaction, CancellationToken cancellationToken, bool force)
    {
        var context = transaction.Context(TransactionOperation.Rollback);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var interceptor in _transactionInterceptors)
                await interceptor.TransactionRollingBackAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            if (force)
                await FailureCleanup.CaptureAsync(exception, () => RollbackProviderAsync(transaction, context, CancellationToken.None)).ConfigureAwait(false);
            await NotifyTransactionFailureAsync(context, exception).ConfigureAwait(false);
            throw;
        }

        try { await transaction.Provider.RollbackAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _faulted = true;
            await NotifyTransactionFailureAsync(context, exception).ConfigureAwait(false);
            throw;
        }
        Complete(transaction);
        foreach (var interceptor in _transactionInterceptors)
            await interceptor.TransactionRolledBackAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task RollbackProviderAsync(CoordinatedTransaction transaction, TransactionContext context, CancellationToken cancellationToken)
    {
        try { await transaction.Provider.RollbackAsync(cancellationToken).ConfigureAwait(false); }
        catch { _faulted = true; throw; }
        Complete(transaction);
        foreach (var interceptor in _transactionInterceptors)
            await interceptor.TransactionRolledBackAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DisposeTransactionAsync(CoordinatedTransaction transaction)
    {
        if (transaction.Disposed) return;
        EnterOperation(allowFaulted: true);
        try { await DisposeTransactionCoreAsync(transaction).ConfigureAwait(false); }
        finally { ExitOperation(); }
    }

    private async Task DisposeTransactionCoreAsync(CoordinatedTransaction transaction)
    {
        if (transaction.Disposed) return;
        Exception? failure = null;
        if (!transaction.IsCompleted)
        {
            try { await RollbackCoreAsync(transaction, CancellationToken.None, force: true).ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
        }

        try
        {
            if (failure != null)
                await FailureCleanup.CaptureAsync(failure, () => DisposeProviderAsync(transaction)).ConfigureAwait(false);
            else
                await DisposeProviderAsync(transaction).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; _faulted = true; }
        finally
        {
            transaction.Disposed = true;
            _transactions.Remove(transaction);
            if (ReferenceEquals(_currentTransaction, transaction)) _currentTransaction = null;
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task DisposeProviderAsync(CoordinatedTransaction transaction)
    {
        try { await transaction.Provider.DisposeAsync().ConfigureAwait(false); }
        catch { _faulted = true; throw; }
    }

    private async Task NotifyTransactionFailureAsync(TransactionContext context, Exception exception)
    {
        foreach (var interceptor in _transactionInterceptors)
            await FailureCleanup.CaptureAsync(exception, () => interceptor.TransactionFailedAsync(context, exception, CancellationToken.None)).ConfigureAwait(false);
    }

    private void Complete(CoordinatedTransaction transaction)
    {
        transaction.Completed = true;
        _currentTransaction = null;
    }

    private void EnterOperation(bool allowFaulted = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent or reentrant unit of work operations are not supported.");
        if (_disposed || (_faulted && !allowFaulted))
        {
            ExitOperation();
            if (_disposed) throw new ObjectDisposedException(nameof(UnitOfWork));
            throw new InvalidOperationException("The persistence session has faulted. Roll back, dispose, and start a new unit of work.");
        }
    }

    private void ExitOperation() => Volatile.Write(ref _busy, 0);

    private void EnsureActive(CoordinatedTransaction transaction)
    {
        if (transaction.Disposed) throw new ObjectDisposedException(nameof(ITransaction));
        if (transaction.IsCompleted || !ReferenceEquals(_currentTransaction, transaction))
            throw new InvalidOperationException("The transaction is no longer active.");
    }

    private static T[] Snapshot<T>(IEnumerable<T>? interceptors, string parameterName) where T : class
    {
        var result = interceptors?.ToArray() ?? Array.Empty<T>();
        if (result.Any(interceptor => interceptor is null))
            throw new ArgumentException("Interceptors cannot contain null.", parameterName);
        return result;
    }

    private sealed class CoordinatedTransaction : ITransaction
    {
        private readonly UnitOfWork _owner;
        private readonly IsolationLevel _isolationLevel;

        public CoordinatedTransaction(UnitOfWork owner, ITransaction provider, IsolationLevel isolationLevel)
        {
            _owner = owner;
            Provider = provider;
            _isolationLevel = isolationLevel;
        }

        public ITransaction Provider { get; }
        public bool Completed { get; set; }
        public bool Disposed { get; set; }
        public bool IsCompleted => Completed || Provider.IsCompleted;
        public TransactionContext Context(TransactionOperation operation) => new TransactionContext(_owner, _isolationLevel, operation, this);
        public Task CommitAsync(CancellationToken cancellationToken = default) => _owner.CommitAsync(this, cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => _owner.RollbackAsync(this, cancellationToken);
        public ValueTask DisposeAsync() => _owner.DisposeTransactionAsync(this);
    }
}
