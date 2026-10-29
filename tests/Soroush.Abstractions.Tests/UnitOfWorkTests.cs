using System.Data;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

namespace Soroush.Abstractions.Tests;

public class UnitOfWorkTests
{
    [Fact]
    public async Task SaveRunsHooksInRegistrationOrderWithTheAffectedCountAndCallerToken()
    {
        var session = new TestSession();
        using var cancellation = new CancellationTokenSource();
        var first = new SaveObserver(session.Events, "first");
        var second = new SaveObserver(session.Events, "second");
        await using var unit = new UnitOfWork(session, new[] { first, second });

        Assert.Equal(3, await unit.SaveChangesAsync(cancellation.Token));

        Assert.Equal(new[] { "first.saving", "second.saving", "provider.save", "first.saved:3", "second.saved:3" }, session.Events);
        Assert.Same(unit, first.Context!.UnitOfWork);
        Assert.Equal(cancellation.Token, first.Token);
        Assert.Equal(cancellation.Token, session.SaveToken);
    }

    [Fact]
    public async Task BeforeSaveFailureNotifiesEveryObserverAndPreservesTheOriginalException()
    {
        var session = new TestSession();
        var original = new InvalidOperationException("before save");
        var secondary = new Exception("failure observer");
        var first = new SaveObserver(session.Events) { BeforeError = original, FailureError = secondary };
        var second = new SaveObserver(session.Events, "second");
        await using var unit = new UnitOfWork(session, new[] { first, second });

        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync()));
        Assert.Equal(0, session.SaveCalls);
        Assert.Same(original, second.Failure);
        Assert.False(second.Token.CanBeCanceled);
        Assert.Same(secondary, Assert.Single(SecondaryExceptions(original)));
        first.BeforeError = null;
        first.FailureError = null;
        Assert.Equal(3, await unit.SaveChangesAsync());
    }

    [Fact]
    public async Task AfterSaveFailureDoesNotReportAProviderFailureOrRepeatTheSave()
    {
        var session = new TestSession();
        var original = new Exception("after save");
        var observer = new SaveObserver(session.Events) { AfterError = original };
        await using var unit = new UnitOfWork(session, new[] { observer });

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.SaveChangesAsync()));
        Assert.Equal(1, session.SaveCalls);
        Assert.Null(observer.Failure);
    }

    [Fact]
    public async Task ProviderSaveFailureFaultsTheCoordinatorButAllowsRollback()
    {
        var session = new TestSession();
        var original = new Exception("save failed");
        var observer = new SaveObserver(session.Events);
        await using var unit = new UnitOfWork(session, new[] { observer });
        await using var transaction = await unit.BeginTransactionAsync();
        session.SaveError = original;

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.SaveChangesAsync()));
        Assert.Same(original, observer.Failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await transaction.RollbackAsync();
        Assert.True(transaction.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        Assert.Equal(1, session.SaveCalls);
    }

    [Fact]
    public async Task CancellationBeforeSaveDoesNotCallTheProviderAndFailureHooksUseAnUncanceledToken()
    {
        var session = new TestSession();
        var observer = new SaveObserver(session.Events);
        await using var unit = new UnitOfWork(session, new[] { observer });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.SaveChangesAsync(new CancellationToken(true)));

        Assert.Equal(0, session.SaveCalls);
        Assert.IsAssignableFrom<OperationCanceledException>(observer.Failure);
        Assert.False(observer.Token.CanBeCanceled);
        Assert.Equal(3, await unit.SaveChangesAsync());
    }

    [Fact]
    public async Task ReentrantOperationsAreRejectedWithoutRecursion()
    {
        var session = new TestSession();
        var observer = new SaveObserver(session.Events);
        await using var unit = new UnitOfWork(session, new[] { observer });
        observer.Before = () => Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());

        Assert.Equal(3, await unit.SaveChangesAsync());
        Assert.Equal(1, session.SaveCalls);
    }

    [Fact]
    public async Task ConcurrentSaveBeginAndDisposalAreRejectedWhileASaveIsInFlight()
    {
        var session = new TestSession();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnSave = async () => { entered.SetResult(true); await release.Task; };
        await using var unit = new UnitOfWork(session);
        var pending = unit.SaveChangesAsync();
        await entered.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BeginTransactionAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.DisposeAsync().AsTask());
        }
        finally { release.SetResult(true); await pending; }
    }

    [Fact]
    public async Task BeginAndCommitRunTheLifecycleHooksAndDoNotSaveImplicitly()
    {
        var session = new TestSession();
        var observer = new TransactionObserver(session.Events);
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        await using var transaction = await unit.BeginTransactionAsync(IsolationLevel.Serializable);

        Assert.Same(transaction, unit.CurrentTransaction);
        Assert.Same(transaction, observer.Context!.Transaction);
        Assert.Equal(IsolationLevel.Serializable, session.IsolationLevel);
        Assert.Equal(IsolationLevel.Serializable, observer.Context.IsolationLevel);
        await transaction.CommitAsync();

        Assert.Equal(new[] { "tx.starting", "provider.begin", "tx.started", "tx.committing", "provider.commit", "tx.committed" }, session.Events);
        Assert.True(transaction.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        Assert.Equal(0, session.SaveCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
    }

    [Fact]
    public async Task NestedTransactionsAreRejectedBeforeCallingHooksOrProvider()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);
        await using var transaction = await unit.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BeginTransactionAsync());
        Assert.Equal(1, session.BeginCalls);
        Assert.Same(transaction, unit.CurrentTransaction);
    }

    [Fact]
    public async Task BeginBeforeHookFailureCanBeCorrectedWithoutFaultingTheSession()
    {
        var session = new TestSession();
        var original = new Exception("starting");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "starting", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.BeginTransactionAsync()));
        Assert.Equal(0, session.BeginCalls);
        Assert.Same(original, observer.Failure);
        Assert.Equal(TransactionOperation.Begin, observer.Context!.Operation);
        Assert.Null(observer.Context.Transaction);
        observer.ErrorStage = null;
        await using var transaction = await unit.BeginTransactionAsync();
        Assert.Same(transaction, unit.CurrentTransaction);
    }

    [Fact]
    public async Task ProviderBeginFailureNotifiesObserversAndFaultsTheCoordinator()
    {
        var original = new Exception("begin");
        var session = new TestSession { BeginError = original };
        var observer = new TransactionObserver(session.Events);
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.BeginTransactionAsync()));
        Assert.Same(original, observer.Failure);
        Assert.Null(unit.CurrentTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task AfterBeginHookFailureRollsBackAndDisposesTheUnreturnedTransaction()
    {
        var session = new TestSession();
        var original = new Exception("started");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "started", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.BeginTransactionAsync()));

        Assert.Null(unit.CurrentTransaction);
        Assert.Null(observer.Failure);
        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Contains("tx.rolledback", session.Events);
    }

    [Fact]
    public async Task ActiveTransactionDisposalRollsBackWithHooksAndIsIdempotent()
    {
        var session = new TestSession();
        var observer = new TransactionObserver(session.Events);
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();
        session.Events.Clear();

        await transaction.DisposeAsync();
        await transaction.DisposeAsync();

        Assert.Equal(new[] { "tx.rollingback", "provider.rollback", "tx.rolledback", "provider.dispose" }, session.Events);
        Assert.True(transaction.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transaction.CommitAsync());
        Assert.False(session.LastTransaction!.RollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task CommitProviderFailureCannotBeRetriedButCanBeRolledBack()
    {
        var session = new TestSession();
        var original = new Exception("commit");
        var observer = new TransactionObserver(session.Events);
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        await using var transaction = await unit.BeginTransactionAsync();
        session.LastTransaction!.CommitError = original;

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.CommitAsync()));
        Assert.False(transaction.IsCompleted);
        Assert.Same(original, observer.Failure);
        Assert.Equal(TransactionOperation.Commit, observer.Context!.Operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        Assert.Equal(1, session.LastTransaction.CommitCalls);
        await transaction.RollbackAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BeginTransactionAsync());
    }

    [Fact]
    public async Task BeforeCommitHookFailureDoesNotCommitAndDisposalRollsBack()
    {
        var session = new TestSession();
        var original = new Exception("committing");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "committing", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.CommitAsync()));
        Assert.Equal(0, session.LastTransaction!.CommitCalls);
        Assert.False(transaction.IsCompleted);
        await transaction.DisposeAsync();
        Assert.Equal(1, session.LastTransaction.RollbackCalls);
    }

    [Fact]
    public async Task AfterCommitHookFailureDoesNotAttemptRollbackOrReportCommitFailure()
    {
        var session = new TestSession();
        var original = new Exception("committed");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "committed", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.ExecuteInTransactionAsync(_ => Task.FromResult(42))));

        Assert.Null(unit.CurrentTransaction);
        Assert.Null(observer.Failure);
        Assert.Equal(1, session.LastTransaction!.CommitCalls);
        Assert.Equal(0, session.LastTransaction.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
    }

    [Fact]
    public async Task ProviderCompletionBeforeACommitErrorPreventsRollbackOfCommittedWork()
    {
        var session = new TestSession();
        var original = new Exception("provider cleanup after commit");
        await using var unit = new UnitOfWork(session);
        ITransaction? transaction = null;

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.ExecuteInTransactionAsync(_ =>
        {
            transaction = unit.CurrentTransaction;
            session.LastTransaction!.AfterCommitError = original;
            return Task.FromResult(42);
        })));

        Assert.True(transaction!.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        Assert.Equal(1, session.LastTransaction!.CommitCalls);
        Assert.Equal(0, session.LastTransaction.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task ProviderCompletionBeforeARollbackErrorIsTerminalAndIsNotRepeatedOnDisposal()
    {
        var session = new TestSession();
        var original = new Exception("provider cleanup after rollback");
        await using var unit = new UnitOfWork(session);
        var transaction = await unit.BeginTransactionAsync();
        session.LastTransaction!.AfterRollbackError = original;

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.RollbackAsync()));

        Assert.True(transaction.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.RollbackAsync());
        await transaction.DisposeAsync();
        Assert.Equal(1, session.LastTransaction.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
    }

    [Fact]
    public async Task AfterRollbackHookFailureDoesNotRepeatTheRollbackDuringDisposal()
    {
        var session = new TestSession();
        var original = new Exception("rolledback");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "rolledback", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.RollbackAsync()));
        Assert.True(transaction.IsCompleted);
        Assert.Null(unit.CurrentTransaction);
        await transaction.DisposeAsync();
        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.Null(observer.Failure);
    }

    [Fact]
    public async Task DisposalStillRollsBackAndReleasesResourcesWhenTheBeforeRollbackHookThrows()
    {
        var session = new TestSession();
        var original = new Exception("rollingback");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "rollingback", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.DisposeAsync().AsTask()));

        Assert.True(transaction.IsCompleted);
        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Same(original, observer.Failure);
        Assert.Null(unit.CurrentTransaction);
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task ProviderRollbackFailureStillReleasesTheHandleAndFaultsTheSession()
    {
        var session = new TestSession();
        var original = new Exception("rollback");
        var observer = new TransactionObserver(session.Events);
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();
        session.LastTransaction!.RollbackError = original;

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.DisposeAsync().AsTask()));

        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Same(original, observer.Failure);
        Assert.Null(unit.CurrentTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task TransactionExecutionRunsOnceThenSavesCommitsAndDisposes()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        var result = await unit.ExecuteInTransactionAsync(token =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.NotNull(unit.CurrentTransaction);
            calls++;
            session.Events.Add("application");
            return Task.FromResult(42);
        }, IsolationLevel.Serializable, cancellation.Token);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "provider.begin", "application", "provider.save", "provider.commit", "provider.dispose" }, session.Events);
        Assert.Equal(cancellation.Token, session.LastTransaction!.CommitToken);
    }

    [Fact]
    public async Task VoidTransactionExecutionAlsoSavesAndCommits()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);

        await unit.ExecuteInTransactionAsync(_ => Task.CompletedTask);

        Assert.Equal(1, session.SaveCalls);
        Assert.Equal(1, session.LastTransaction!.CommitCalls);
    }

    [Fact]
    public async Task ApplicationFailureIsPreservedWhenFailureObserversAndCleanupAlsoThrow()
    {
        var session = new TestSession();
        var original = new Exception("application");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "rollingback", Error = new Exception("hook"), FailureError = new Exception("failure observer") };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });

        var exception = await Assert.ThrowsAsync<Exception>(() => unit.ExecuteInTransactionAsync<int>(_ => throw original));

        Assert.Same(original, exception);
        Assert.NotEmpty(SecondaryExceptions(original));
        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Equal(0, session.SaveCalls);
        Assert.Equal(0, session.LastTransaction.CommitCalls);
        Assert.False(observer.FailureToken.CanBeCanceled);
    }

    [Fact]
    public async Task CancellationInsideTheApplicationUsesAnUncanceledCleanupToken()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.ExecuteInTransactionAsync(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(42);
        }, cancellationToken: cancellation.Token));

        Assert.Equal(0, session.SaveCalls);
        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.False(session.LastTransaction.RollbackToken.CanBeCanceled);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
    }

    [Fact]
    public async Task CancellationDuringBeginSkipsApplicationWorkAndCleansUpTheTransaction()
    {
        var session = new TestSession();
        using var cancellation = new CancellationTokenSource();
        await using var unit = new UnitOfWork(session,
            transactionInterceptors: new[] { new CancelAfterBegin(cancellation) });
        var called = false;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.ExecuteInTransactionAsync(_ =>
        {
            called = true;
            return Task.FromResult(42);
        }, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(called);
        Assert.Equal(0, session.SaveCalls);
        Assert.Equal(0, session.LastTransaction!.CommitCalls);
        Assert.Equal(1, session.LastTransaction.RollbackCalls);
        Assert.False(session.LastTransaction.RollbackToken.CanBeCanceled);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Null(unit.CurrentTransaction);
    }

    [Fact]
    public async Task SaveFailureInManagedExecutionRollsBackAndDoesNotCommit()
    {
        var original = new Exception("save");
        var session = new TestSession { SaveError = original };
        await using var unit = new UnitOfWork(session);

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => unit.ExecuteInTransactionAsync(_ => Task.FromResult(42))));

        Assert.Equal(1, session.LastTransaction!.RollbackCalls);
        Assert.Equal(0, session.LastTransaction.CommitCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
    }

    [Fact]
    public async Task ManagedExecutionRejectsACallbackThatCompletesItsTransactionBeforeSaving()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteInTransactionAsync(async _ =>
        {
            await unit.CurrentTransaction!.CommitAsync();
            return 42;
        }));

        Assert.Equal(0, session.SaveCalls);
        Assert.Equal(1, session.LastTransaction!.CommitCalls);
        Assert.Equal(0, session.LastTransaction.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
    }

    [Fact]
    public async Task UnitDisposalReleasesCompletedAndActiveTransactionsAndContinuesAfterCleanupFailure()
    {
        var session = new TestSession();
        var unit = new UnitOfWork(session);
        var completed = await unit.BeginTransactionAsync();
        var first = session.LastTransaction!;
        await completed.CommitAsync();
        await unit.BeginTransactionAsync();
        var second = session.LastTransaction!;
        first.DisposeError = new Exception("first dispose");
        second.DisposeError = new Exception("second dispose");

        var error = await Assert.ThrowsAsync<Exception>(() => unit.DisposeAsync().AsTask());

        Assert.Same(second.DisposeError, error);
        Assert.Contains(first.DisposeError, SecondaryExceptions(error));
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(0, first.RollbackCalls);
        Assert.Equal(1, second.RollbackCalls);
        Assert.Null(unit.CurrentTransaction);
        await unit.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => unit.SaveChangesAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => unit.BeginTransactionAsync());
    }

    [Fact]
    public void ConstructorValidatesInputs()
    {
        var session = new TestSession();
        Assert.Throws<ArgumentNullException>(() => new UnitOfWork(null!));
        Assert.Throws<ArgumentException>(() => new UnitOfWork(session, new ISaveChangesInterceptor[] { null! }));
        Assert.Throws<ArgumentException>(() => new UnitOfWork(session, transactionInterceptors: new ITransactionInterceptor[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new SaveChangesContext(null!));
        Assert.Throws<ArgumentNullException>(() => new TransactionContext(null!, IsolationLevel.ReadCommitted, TransactionOperation.Begin));
    }

    [Fact]
    public async Task InterceptorCollectionsAreSnapshottedAtConstruction()
    {
        var session = new TestSession();
        var saves = new List<ISaveChangesInterceptor> { new SaveObserver(session.Events) };
        var transactions = new List<ITransactionInterceptor> { new TransactionObserver(session.Events) };
        await using var unit = new UnitOfWork(session, saves, transactions);
        saves.Clear();
        transactions.Clear();

        await unit.ExecuteInTransactionAsync(_ => Task.CompletedTask);

        Assert.Contains("save.saving", session.Events);
        Assert.Contains("tx.committed", session.Events);
    }

    [Fact]
    public async Task CanceledBeginAndCommitDoNotReachTheProviderAndLeaveCleanupAvailable()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unit.BeginTransactionAsync(cancellationToken: canceled));
        Assert.Equal(0, session.BeginCalls);
        await using var transaction = await unit.BeginTransactionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(canceled));
        Assert.Equal(0, session.LastTransaction!.CommitCalls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.RollbackAsync(canceled));
        Assert.Equal(0, session.LastTransaction.RollbackCalls);
        await transaction.RollbackAsync();
        Assert.True(transaction.IsCompleted);
    }

    [Fact]
    public async Task ProviderCancellationDuringCommitFaultsTheSessionAndManagedExecutionRollsBack()
    {
        var session = new TestSession();
        var original = new OperationCanceledException();
        await using var unit = new UnitOfWork(session);

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => unit.ExecuteInTransactionAsync(_ =>
        {
            session.LastTransaction!.CommitError = original;
            return Task.FromResult(42);
        }));

        Assert.Same(original, error);
        Assert.Equal(1, session.LastTransaction!.CommitCalls);
        Assert.Equal(1, session.LastTransaction.RollbackCalls);
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task ForcedRollbackPreservesHookFailureAndRetainsProviderAndDisposalFailures()
    {
        var session = new TestSession();
        var original = new Exception("hook");
        var observer = new TransactionObserver(session.Events) { ErrorStage = "rollingback", Error = original };
        await using var unit = new UnitOfWork(session, transactionInterceptors: new[] { observer });
        var transaction = await unit.BeginTransactionAsync();
        session.LastTransaction!.RollbackError = new Exception("provider rollback");
        session.LastTransaction.DisposeError = new Exception("provider dispose");

        Assert.Same(original, await Assert.ThrowsAsync<Exception>(() => transaction.DisposeAsync().AsTask()));

        Assert.Contains(session.LastTransaction.RollbackError, SecondaryExceptions(original));
        Assert.Contains(session.LastTransaction.DisposeError, SecondaryExceptions(original));
        Assert.Equal(1, session.LastTransaction.DisposeCalls);
        Assert.Null(unit.CurrentTransaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task TransactionExecutionRejectsNullArgumentsBeforeBeginningATransaction()
    {
        var session = new TestSession();
        await using var unit = new UnitOfWork(session);

        await Assert.ThrowsAsync<ArgumentNullException>(() => UnitOfWorkExtensions.ExecuteInTransactionAsync<int>(null!, _ => Task.FromResult(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => unit.ExecuteInTransactionAsync<int>(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => unit.ExecuteInTransactionAsync((Func<CancellationToken, Task>)null!));
        Assert.Equal(0, session.BeginCalls);
    }

    private static List<Exception> SecondaryExceptions(Exception exception)
        => Assert.IsType<List<Exception>>(exception.Data["Soroush.Persistence.SecondaryExceptions"]);

    internal sealed class TestSession : IPersistenceSession
    {
        public List<string> Events { get; } = new();
        public Exception? SaveError { get; set; }
        public Exception? BeginError { get; set; }
        public Func<Task>? OnSave { get; set; }
        public int SaveCalls { get; private set; }
        public int BeginCalls { get; private set; }
        public TestTransaction? LastTransaction { get; private set; }
        public CancellationToken SaveToken { get; private set; }
        public IsolationLevel IsolationLevel { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("provider.save");
            SaveCalls++;
            SaveToken = cancellationToken;
            if (OnSave != null) await OnSave();
            if (SaveError != null) throw SaveError;
            return 3;
        }

        public Task<ITransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
        {
            Events.Add("provider.begin");
            BeginCalls++;
            IsolationLevel = isolationLevel;
            if (BeginError != null) throw BeginError;
            LastTransaction = new TestTransaction(Events);
            return Task.FromResult<ITransaction>(LastTransaction);
        }
    }

    internal sealed class TestTransaction(List<string> events) : ITransaction
    {
        public Exception? CommitError { get; set; }
        public Exception? RollbackError { get; set; }
        public Exception? AfterCommitError { get; set; }
        public Exception? AfterRollbackError { get; set; }
        public Exception? DisposeError { get; set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool IsCompleted { get; private set; }
        public CancellationToken CommitToken { get; private set; }
        public CancellationToken RollbackToken { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            events.Add("provider.commit");
            CommitCalls++;
            CommitToken = cancellationToken;
            if (CommitError != null) throw CommitError;
            IsCompleted = true;
            if (AfterCommitError != null) throw AfterCommitError;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            events.Add("provider.rollback");
            RollbackCalls++;
            RollbackToken = cancellationToken;
            if (RollbackError != null) throw RollbackError;
            IsCompleted = true;
            if (AfterRollbackError != null) throw AfterRollbackError;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            events.Add("provider.dispose");
            DisposeCalls++;
            if (DisposeError != null) throw DisposeError;
            return default;
        }
    }

    private sealed class SaveObserver(List<string> events, string name = "save") : SaveChangesInterceptor
    {
        public Exception? BeforeError { get; set; }
        public Exception? AfterError { get; set; }
        public Exception? FailureError { get; set; }
        public Func<Task>? Before { get; set; }
        public Exception? Failure { get; private set; }
        public SaveChangesContext? Context { get; private set; }
        public CancellationToken Token { get; private set; }

        public override async Task SavingChangesAsync(SaveChangesContext context, CancellationToken cancellationToken)
        {
            events.Add($"{name}.saving");
            Context = context;
            Token = cancellationToken;
            if (BeforeError != null) throw BeforeError;
            if (Before != null) await Before();
        }

        public override Task SavedChangesAsync(SaveChangesContext context, int affectedEntries, CancellationToken cancellationToken)
        {
            events.Add($"{name}.saved:{affectedEntries}");
            if (AfterError != null) throw AfterError;
            return Task.CompletedTask;
        }

        public override Task SaveChangesFailedAsync(SaveChangesContext context, Exception exception, CancellationToken cancellationToken)
        {
            events.Add($"{name}.failed");
            Failure = exception;
            Token = cancellationToken;
            if (FailureError != null) throw FailureError;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelAfterBegin(CancellationTokenSource cancellation) : TransactionInterceptor
    {
        public override Task TransactionStartedAsync(TransactionContext context, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionObserver(List<string> events) : TransactionInterceptor
    {
        public string? ErrorStage { get; set; }
        public Exception? Error { get; set; }
        public Exception? FailureError { get; set; }
        public Exception? Failure { get; private set; }
        public TransactionContext? Context { get; private set; }
        public CancellationToken FailureToken { get; private set; }

        public override Task TransactionStartingAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("starting", context);
        public override Task TransactionStartedAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("started", context);
        public override Task TransactionCommittingAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("committing", context);
        public override Task TransactionCommittedAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("committed", context);
        public override Task TransactionRollingBackAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("rollingback", context);
        public override Task TransactionRolledBackAsync(TransactionContext context, CancellationToken cancellationToken) => Observe("rolledback", context);
        public override Task TransactionFailedAsync(TransactionContext context, Exception exception, CancellationToken cancellationToken)
        {
            events.Add("tx.failed");
            Context = context;
            Failure = exception;
            FailureToken = cancellationToken;
            if (FailureError != null) throw FailureError;
            return Task.CompletedTask;
        }

        private Task Observe(string stage, TransactionContext context)
        {
            events.Add($"tx.{stage}");
            Context = context;
            if (stage == ErrorStage) throw Error!;
            return Task.CompletedTask;
        }
    }
}
