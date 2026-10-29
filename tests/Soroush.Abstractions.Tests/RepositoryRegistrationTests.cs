using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Soroush.Domain.Abstractions;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;
using Session = Soroush.Abstractions.Tests.PersistenceDependencyInjectionTests.Session;

namespace Soroush.Abstractions.Tests;

public class RepositoryRegistrationTests
{
    [Fact]
    public async Task WriteAndReadContractsShareRepositoryStateAndTheUnitOfWorkSession()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>()
            .AddSoroushRepository<Note, Guid, Repository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var write = scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>();
        var read = scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>();
        var session = scope.ServiceProvider.GetRequiredService<Session>();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var note = new Note { Id = Guid.NewGuid() };

        await unit.ExecuteInTransactionAsync(token => write.AddAsync(note, token));

        Assert.Same(note, await read.GetByIdAsync(note.Id));
        Assert.Same(session, Assert.IsType<Repository<Note, Guid>>(write).Session);
        Assert.Same(write, scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        Assert.Same(read, scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>());
        Assert.Equal(1, session.SaveCalls);
    }

    [Fact]
    public async Task ReadViewForwardsSpecificationsPagingStreamingAndCancellationToTheCanonicalRepository()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>().AddSoroushRepository<Note, Guid, Repository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var write = Assert.IsType<Repository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        var read = scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>();
        var first = new Note { Id = Guid.NewGuid() };
        var second = new Note { Id = Guid.NewGuid() };
        await write.AddAsync(first);
        await write.AddAsync(second);
        var specification = new Specification<Note>(note => note.Id == first.Id);
        var page = new PageRequest();
        using var cancellation = new CancellationTokenSource();

        Assert.Same(first, await read.GetByIdAsync(first.Id, cancellation.Token));
        Assert.Same(first, Assert.Single(await read.ListAsync(specification, cancellation.Token)));
        Assert.Same(first, Assert.Single((await read.PageAsync(specification, page, cancellation.Token)).Items));
        Assert.Same(page, write.LastPage);
        Assert.Equal(1, await read.CountAsync(specification, cancellation.Token));
        Assert.True(await read.AnyAsync(specification, cancellation.Token));
        var streamed = new List<Note>();
        await foreach (var note in read.StreamAsync(specification, cancellation.Token)) streamed.Add(note);
        Assert.Same(first, Assert.Single(streamed));
        Assert.Same(specification, write.LastSpecification);
        Assert.Equal(cancellation.Token, write.LastToken);
    }

    [Fact]
    public async Task ResolvingBothContractsDisposesTheRepositoryAndSessionExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>().AddSoroushRepository<Note, Guid, Repository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>();
        var repository = Assert.IsType<Repository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());

        await scope.DisposeAsync();

        Assert.Equal(1, repository.DisposeCalls);
        Assert.Equal(1, repository.Session.DisposeCalls);
    }

    [Fact]
    public async Task OpenGenericRepositoriesAreScopedPerEntityAndPerScope()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>().AddSoroushRepository(typeof(Repository<,>));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var notes = first.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>();
        var messages = first.ServiceProvider.GetRequiredService<IRepository<Message, int>>();
        var note = new Note { Id = Guid.NewGuid() };
        await notes.AddAsync(note);

        Assert.Same(note, await first.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>().GetByIdAsync(note.Id));
        Assert.IsType<Repository<Message, int>>(messages);
        Assert.NotSame(notes, second.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        Assert.Null(await second.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>().GetByIdAsync(note.Id));
    }

    [Fact]
    public async Task ClosedRepositoryOverridesAnOpenGenericDefaultForBothContracts()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>()
            .AddSoroushRepository(typeof(Repository<,>))
            .AddSoroushRepository<Note, Guid, AlternateRepository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var notes = scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>();
        var note = new Note { Id = Guid.NewGuid() };
        await notes.AddAsync(note);

        Assert.IsType<AlternateRepository<Note, Guid>>(notes);
        Assert.Same(note, await scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>().GetByIdAsync(note.Id));
        Assert.IsType<Repository<Message, int>>(scope.ServiceProvider.GetRequiredService<IRepository<Message, int>>());
    }

    [Fact]
    public async Task ClosedAndOpenReadOnlyRegistrationsDoNotAddWriteAccess()
    {
        var services = new ServiceCollection();
        services.AddSoroushUnitOfWork<Session>()
            .AddSoroushReadRepository(typeof(ReadRepository<,>))
            .AddSoroushReadRepository<Note, Guid, ReadRepository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<ReadRepository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>());
        Assert.IsType<ReadRepository<Message, int>>(scope.ServiceProvider.GetRequiredService<IReadRepository<Message, int>>());
        Assert.Null(scope.ServiceProvider.GetService<IRepository<Note, Guid>>());
        Assert.Null(scope.ServiceProvider.GetService<IRepository<Message, int>>());
    }

    [Fact]
    public async Task ExplicitReadAdapterAndWriteAdapterArePreservedByRepeatedRegistration()
    {
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>()
            .AddSoroushReadRepository<Note, Guid, ReadRepository<Note, Guid>>()
            .AddSoroushRepository<Note, Guid, AlternateRepository<Note, Guid>>()
            .AddSoroushRepository<Note, Guid, Repository<Note, Guid>>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<ReadRepository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>());
        Assert.IsType<AlternateRepository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        Assert.Single(scope.ServiceProvider.GetServices<IRepository<Note, Guid>>());
        Assert.Single(scope.ServiceProvider.GetServices<IReadRepository<Note, Guid>>());
    }

    [Fact]
    public async Task ScanningRegistersClosedRepositoriesOnceAndSkipsAbstractTypes()
    {
        var assembly = CreateAssembly(("Notes", typeof(Repository<Note, Guid>), false), ("AbstractNotes", typeof(Repository<Note, Guid>), true),
            ("ReadMessages", typeof(ReadRepository<Message, int>), false));
        var services = new ServiceCollection();
        services.AddSoroushPersistence<Session>(assembly, assembly).AddSoroushRepositories(assembly);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var scope = provider.CreateAsyncScope();
        var notes = Assert.IsAssignableFrom<Repository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        var read = scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>();
        var note = new Note { Id = Guid.NewGuid() };
        await notes.AddAsync(note);

        Assert.Same(note, await read.GetByIdAsync(note.Id));
        Assert.Single(scope.ServiceProvider.GetServices<IRepository<Note, Guid>>());
        Assert.Single(scope.ServiceProvider.GetServices<IReadRepository<Note, Guid>>());
        Assert.IsAssignableFrom<ReadRepository<Message, int>>(scope.ServiceProvider.GetRequiredService<IReadRepository<Message, int>>());
        Assert.Null(scope.ServiceProvider.GetService<IRepository<Message, int>>());
        await scope.DisposeAsync();
        Assert.Equal(1, notes.DisposeCalls);
    }

    [Fact]
    public void AmbiguousScanningFailsWithoutPartiallyChangingTheCollection()
    {
        var assembly = CreateAssembly(("First", typeof(Repository<Note, Guid>), false), ("Second", typeof(Repository<Note, Guid>), false));
        var services = new ServiceCollection();
        services.AddSingleton(new object());
        var before = services.ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddSoroushPersistence<Session>(assembly));

        Assert.Contains("Multiple repositories", error.Message);
        Assert.Equal(before, services);
    }

    [Fact]
    public async Task ExplicitContractsResolveAmbiguousScanCandidates()
    {
        var assembly = CreateAssembly(("First", typeof(Repository<Note, Guid>), false), ("Second", typeof(Repository<Note, Guid>), false));
        var services = new ServiceCollection();
        services.AddSoroushRepository<Note, Guid, AlternateRepository<Note, Guid>>()
            .AddSoroushPersistence<Session>(assembly);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<AlternateRepository<Note, Guid>>(scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>());
        Assert.Single(scope.ServiceProvider.GetServices<IReadRepository<Note, Guid>>());
    }

    [Fact]
    public async Task ExistingWriteRegistrationAlsoSuppliesTheDefaultReadView()
    {
        var assembly = CreateAssembly(("Notes", typeof(Repository<Note, Guid>), false));
        var services = new ServiceCollection();
        services.AddScoped<IRepository<Note, Guid>, AlternateRepository<Note, Guid>>();
        services.AddSoroushPersistence<Session>(assembly);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var notes = scope.ServiceProvider.GetRequiredService<IRepository<Note, Guid>>();
        var note = new Note { Id = Guid.NewGuid() };
        await notes.AddAsync(note);

        Assert.IsType<AlternateRepository<Note, Guid>>(notes);
        Assert.Same(note, await scope.ServiceProvider.GetRequiredService<IReadRepository<Note, Guid>>().GetByIdAsync(note.Id));
    }

    [Fact]
    public void InvalidScanArgumentsDoNotAddAnyRegistrations()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddSoroushRepositories(null!));
        Assert.Throws<ArgumentException>(() => services.AddSoroushPersistence<Session>(new Assembly[] { null! }));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(typeof(Repository<Note, Guid>))]
    [InlineData(typeof(ReadRepository<,>))]
    [InlineData(typeof(AbstractRepository<,>))]
    [InlineData(typeof(SwappedRepository<,>))]
    [InlineData(typeof(List<>))]
    [InlineData(typeof(IRepository<,>))]
    [InlineData(typeof(int))]
    public void InvalidOpenGenericWriteAdaptersAreRejectedBeforeRegistration(Type type)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddSoroushRepository(type));
        Assert.Empty(services);
    }

    [Fact]
    public void NullCollectionsAndTypesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushPersistence<Session>(null!));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushRepositories(null!));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushRepository<Note, Guid, Repository<Note, Guid>>(null!));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushReadRepository<Note, Guid, ReadRepository<Note, Guid>>(null!));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushRepository(null!, typeof(Repository<,>)));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushReadRepository(null!, typeof(ReadRepository<,>)));
        Assert.Throws<ArgumentNullException>(() => Persistence.ServiceCollectionExtensions.AddSoroushConnectionFactory<ConnectionFactory>(null!));
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddSoroushRepository(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddSoroushReadRepository(null!));
        Assert.Throws<ArgumentException>(() => services.AddSoroushReadRepository(typeof(List<>)));
        Assert.Empty(services);
    }

    [Fact]
    public async Task ConnectionFactoryRegistrationIsScopedAndPreservesExistingMappings()
    {
        var services = new ServiceCollection();
        services.AddSoroushConnectionFactory<ConnectionFactory>().AddSoroushConnectionFactory<ConnectionFactory>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        Assert.IsType<ConnectionFactory>(first.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
        Assert.Same(first.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), first.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), second.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
        Assert.Single(first.ServiceProvider.GetServices<IDbConnectionFactory>());
    }

    // Isolate scan fixtures from other test repositories without adding fixture projects to the solution.
    private static Assembly CreateAssembly(params (string Name, Type Base, bool Abstract)[] types)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RepositoryFixtures" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixtures");
        foreach (var item in types)
        {
            var type = module.DefineType(item.Name, TypeAttributes.Public | (item.Abstract ? TypeAttributes.Abstract : 0), item.Base);
            var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(Session) });
            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, item.Base.GetConstructor(new[] { typeof(Session) })!);
            il.Emit(OpCodes.Ret);
            type.CreateType();
        }
        return assembly;
    }

    public sealed class Note : IEntity<Guid>
    {
        public Guid Id { get; init; }
    }

    public sealed class Message : IEntity<int>
    {
        public int Id { get; init; }
    }

    public class ReadRepository<TEntity, TKey>(Session session) : IReadRepository<TEntity, TKey>, IAsyncDisposable
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        protected List<TEntity> Items { get; } = new();
        public Session Session { get; } = session;
        public int DisposeCalls { get; private set; }
        public ISpecification<TEntity>? LastSpecification { get; private set; }
        public PageRequest? LastPage { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            return Task.FromResult(Items.FirstOrDefault(item => EqualityComparer<TKey>.Default.Equals(item.Id, id)));
        }

        public Task<IReadOnlyList<TEntity>> ListAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        {
            LastSpecification = specification;
            LastToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<TEntity>>(Items.Where(specification.Criteria.Compile()).ToArray());
        }

        public async Task<PagedResult<TEntity>> PageAsync(ISpecification<TEntity> specification, PageRequest page, CancellationToken cancellationToken = default)
        {
            LastPage = page;
            var matches = await ListAsync(specification, cancellationToken);
            return new PagedResult<TEntity>(matches.Skip(page.Offset).Take(page.PageSize), matches.Count, page);
        }

        public async Task<long> CountAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
            => (await ListAsync(specification, cancellationToken)).Count;

        public async Task<bool> AnyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
            => (await ListAsync(specification, cancellationToken)).Count > 0;

        public async IAsyncEnumerable<TEntity> StreamAsync(ISpecification<TEntity> specification, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in await ListAsync(specification, cancellationToken)) yield return item;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return default;
        }
    }

    public class Repository<TEntity, TKey>(Session session) : ReadRepository<TEntity, TKey>(session), IRepository<TEntity, TKey>
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            Items.Add(entity);
            return Task.CompletedTask;
        }
        public void Update(TEntity entity) { }
        public void Remove(TEntity entity) => Items.Remove(entity);
    }

    public class AlternateRepository<TEntity, TKey>(Session session) : Repository<TEntity, TKey>(session)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull;

    public abstract class AbstractRepository<TEntity, TKey>(Session session) : Repository<TEntity, TKey>(session)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull;

    public class SwappedRepository<TKey, TEntity>(Session session) : Repository<TEntity, TKey>(session)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull;

    public sealed class ConnectionFactory : IDbConnectionFactory
    {
        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No database is opened by registration tests.");
    }
}
