using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

namespace Soroush.Abstractions.Tests;

public class PersistenceDependencyInjectionTests
{
    [Fact]
    public async Task ScopedSessionIsSharedAndDisposedOnceAfterTheCoordinatorRollsBack()
    {
        var services = new ServiceCollection();
        services.AddSoroushUnitOfWork<Session>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var session = scope.ServiceProvider.GetRequiredService<Session>();

        Assert.Same(unit, scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        await unit.SaveChangesAsync();
        await unit.BeginTransactionAsync();
        Assert.Equal(1, session.SaveCalls);

        await scope.DisposeAsync();

        Assert.Equal(new[] { "save", "rollback", "transaction.dispose", "session.dispose" }, session.Events);
        Assert.Equal(1, session.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => unit.SaveChangesAsync());
    }

    [Fact]
    public async Task RepeatedRegistrationPreservesInterceptorOrderWithoutDuplicates()
    {
        var services = new ServiceCollection();
        services.AddSoroushUnitOfWork<Session>()
            .AddSaveChangesInterceptor<FirstSaveObserver>()
            .AddSaveChangesInterceptor<SecondSaveObserver>()
            .AddTransactionInterceptor<TransactionObserver>();
        services.AddSoroushUnitOfWork<Session>()
            .AddSaveChangesInterceptor<FirstSaveObserver>()
            .AddTransactionInterceptor<TransactionObserver>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<Session>();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unit.ExecuteInTransactionAsync(_ => Task.CompletedTask);

        Assert.Equal(new[] { "starting", "first", "second", "save", "transaction.dispose" }, session.Events);
        Assert.Equal(2, scope.ServiceProvider.GetServices<ISaveChangesInterceptor>().Count());
        Assert.Single(scope.ServiceProvider.GetServices<ITransactionInterceptor>());
    }

    [Fact]
    public async Task SeparateScopesHaveSeparateSessionsAndCoordinators()
    {
        var services = new ServiceCollection();
        services.AddSoroushUnitOfWork<Session>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        Assert.NotSame(first.ServiceProvider.GetRequiredService<Session>(), second.ServiceProvider.GetRequiredService<Session>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IUnitOfWork>(), second.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public async Task ExistingSessionAndUnitOfWorkRegistrationsArePreserved()
    {
        var services = new ServiceCollection();
        var session = new Session();
        await using var unit = new UnitOfWork(session);
        services.AddScoped(_ => session);
        services.AddScoped<IUnitOfWork>(_ => unit);
        services.AddSoroushUnitOfWork<Session>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        Assert.Same(session, scope.ServiceProvider.GetRequiredService<Session>());
        Assert.Same(unit, scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void RegistrationRejectsANullCollection()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddSoroushUnitOfWork<Session>(null!));
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddSaveChangesInterceptor<FirstSaveObserver>(null!));
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddTransactionInterceptor<TransactionObserver>(null!));
    }

    public sealed class Session : IPersistenceSession, IAsyncDisposable
    {
        public List<string> Events { get; } = new();
        public int SaveCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("save");
            SaveCalls++;
            return Task.FromResult(1);
        }

        public Task<ITransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
            => Task.FromResult<ITransaction>(new Transaction(Events));

        public ValueTask DisposeAsync()
        {
            Events.Add("session.dispose");
            DisposeCalls++;
            return default;
        }
    }

    private sealed class Transaction(List<string> events) : ITransaction
    {
        public bool IsCompleted { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            return Task.CompletedTask;
        }
        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            events.Add("rollback");
            IsCompleted = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            events.Add("transaction.dispose");
            return default;
        }
    }

    public sealed class FirstSaveObserver(Session session) : SaveChangesInterceptor
    {
        public override Task SavingChangesAsync(SaveChangesContext context, CancellationToken cancellationToken)
        {
            session.Events.Add("first");
            return Task.CompletedTask;
        }
    }

    public sealed class SecondSaveObserver(Session session) : SaveChangesInterceptor
    {
        public override Task SavingChangesAsync(SaveChangesContext context, CancellationToken cancellationToken)
        {
            session.Events.Add("second");
            return Task.CompletedTask;
        }
    }

    public sealed class TransactionObserver(Session session) : TransactionInterceptor
    {
        public override Task TransactionStartingAsync(TransactionContext context, CancellationToken cancellationToken)
        {
            session.Events.Add("starting");
            return Task.CompletedTask;
        }
    }
}
