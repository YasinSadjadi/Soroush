using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>Registers scoped persistence sessions, repositories, connections, and lifecycle interceptors.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers a session, unit of work, and closed repository implementations from the supplied assemblies.
    /// Open generic repository adapters must be registered explicitly.</summary>
    public static IServiceCollection AddSoroushPersistence<TSession>(this IServiceCollection services, params Assembly[] assemblies)
        where TSession : class, IPersistenceSession
    {
        services.AddSoroushRepositories(assemblies);
        return services.AddSoroushUnitOfWork<TSession>();
    }

    /// <summary>Registers a scoped write repository and a read-only view backed by the same instance.
    /// Existing registrations are preserved. The container owns the write repository once; the view does not dispose it.</summary>
    public static IServiceCollection AddSoroushRepository<TEntity, TKey, TRepository>(this IServiceCollection services)
        where TEntity : class, Soroush.Domain.Abstractions.IEntity<TKey>
        where TKey : notnull
        where TRepository : class, IRepository<TEntity, TKey>
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        AddRepository(services, typeof(IRepository<TEntity, TKey>), typeof(TRepository));
        return services;
    }

    /// <summary>Registers a scoped read-only repository without adding a write repository.
    /// Register this before a writable default to select a separate read adapter.</summary>
    public static IServiceCollection AddSoroushReadRepository<TEntity, TKey, TRepository>(this IServiceCollection services)
        where TEntity : class, Soroush.Domain.Abstractions.IEntity<TKey>
        where TKey : notnull
        where TRepository : class, IReadRepository<TEntity, TKey>
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddScoped<IReadRepository<TEntity, TKey>, TRepository>();
        return services;
    }

    /// <summary>Registers an open generic repository adapter, for example typeof(SqlRepository&lt;,&gt;).
    /// Entity and key parameters must appear in that order, with constraints compatible with the repository contract.</summary>
    public static IServiceCollection AddSoroushRepository(this IServiceCollection services, Type repositoryType)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        ValidateOpenRepository(repositoryType, typeof(IRepository<,>));
        services.TryAdd(ServiceDescriptor.Scoped(typeof(IRepository<,>), repositoryType));
        services.TryAdd(ServiceDescriptor.Scoped(typeof(IReadRepository<,>), typeof(ReadRepositoryView<,>)));
        return services;
    }

    /// <summary>Registers an open generic read-only adapter, for example typeof(SqlReadRepository&lt;,&gt;).
    /// Entity and key parameters must appear in that order, with compatible constraints.</summary>
    public static IServiceCollection AddSoroushReadRepository(this IServiceCollection services, Type repositoryType)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        ValidateOpenRepository(repositoryType, typeof(IReadRepository<,>));
        services.TryAdd(ServiceDescriptor.Scoped(typeof(IReadRepository<,>), repositoryType));
        return services;
    }

    /// <summary>Scans closed, concrete repository adapters. Duplicate assemblies are ignored and existing
    /// service registrations are preserved. Ambiguous contracts must be registered explicitly before scanning.</summary>
    /// <remarks>Only IRepository and IReadRepository contracts are scanned. Application-specific interfaces,
    /// open generic adapters, sessions, and provider dependencies require explicit registration.
    /// Validation completes before any services are added.</remarks>
    public static IServiceCollection AddSoroushRepositories(this IServiceCollection services, params Assembly[] assemblies)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (assemblies.Any(assembly => assembly is null))
            throw new ArgumentException("Assemblies cannot contain null.", nameof(assemblies));

        var candidates = assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters)
            .SelectMany(implementation => implementation.GetInterfaces()
                .Where(IsRepositoryContract).Select(contract => new { Contract = contract, Implementation = implementation }))
            .GroupBy(candidate => candidate.Contract)
            .Select(group => new { Contract = group.Key, Implementations = group.Select(candidate => candidate.Implementation).Distinct().ToArray() })
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (!services.Any(service => service.ServiceType == candidate.Contract) && candidate.Implementations.Length > 1)
                throw new InvalidOperationException($"Multiple repositories implement {candidate.Contract}: {string.Join(", ", candidate.Implementations.Select(type => type.FullName))}. Register the contract explicitly before scanning.");
        }

        // Writes first, so the inherited read contract is a non-owning view instead of a second repository.
        foreach (var candidate in candidates.Where(candidate => candidate.Contract.GetGenericTypeDefinition() == typeof(IRepository<,>)))
            AddRepository(services, candidate.Contract, candidate.Implementations[0]);
        foreach (var candidate in candidates.Where(candidate => candidate.Contract.GetGenericTypeDefinition() == typeof(IReadRepository<,>)))
            services.TryAdd(ServiceDescriptor.Scoped(candidate.Contract, candidate.Implementations[0]));
        return services;
    }

    /// <summary>Registers a scoped connection factory. The caller still owns each connection it opens.</summary>
    public static IServiceCollection AddSoroushConnectionFactory<TFactory>(this IServiceCollection services)
        where TFactory : class, IDbConnectionFactory
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddScoped<IDbConnectionFactory, TFactory>();
        return services;
    }

    /// <summary>Registers the concrete adapter once and shares its scoped instance with the coordinator and repositories.
    /// The DI scope owns the adapter and disposes it after the coordinator.</summary>
    public static IServiceCollection AddSoroushUnitOfWork<TSession>(this IServiceCollection services)
        where TSession : class, IPersistenceSession
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddScoped<TSession>();
        services.TryAddScoped<IUnitOfWork>(provider => new UnitOfWork(
            provider.GetRequiredService<TSession>(),
            provider.GetServices<ISaveChangesInterceptor>(),
            provider.GetServices<ITransactionInterceptor>()));
        return services;
    }

    /// <summary>Adds one scoped save interceptor in registration order.</summary>
    public static IServiceCollection AddSaveChangesInterceptor<TInterceptor>(this IServiceCollection services)
        where TInterceptor : class, ISaveChangesInterceptor
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISaveChangesInterceptor, TInterceptor>());
        return services;
    }

    /// <summary>Adds one scoped transaction interceptor in registration order.</summary>
    public static IServiceCollection AddTransactionInterceptor<TInterceptor>(this IServiceCollection services)
        where TInterceptor : class, ITransactionInterceptor
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ITransactionInterceptor, TInterceptor>());
        return services;
    }

    private static void AddRepository(IServiceCollection services, Type contract, Type implementation)
    {
        services.TryAdd(ServiceDescriptor.Scoped(contract, implementation));
        var arguments = contract.GetGenericArguments();
        services.TryAdd(ServiceDescriptor.Scoped(typeof(IReadRepository<,>).MakeGenericType(arguments),
            typeof(ReadRepositoryView<,>).MakeGenericType(arguments)));
    }

    private static bool IsRepositoryContract(Type type)
        => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IRepository<,>) || type.GetGenericTypeDefinition() == typeof(IReadRepository<,>));

    private static void ValidateOpenRepository(Type repositoryType, Type contract)
    {
        if (repositoryType is null) throw new ArgumentNullException(nameof(repositoryType));
        if (!repositoryType.IsClass || repositoryType.IsAbstract || !repositoryType.IsGenericTypeDefinition || repositoryType.GetGenericArguments().Length != 2 ||
            !repositoryType.GetInterfaces().Any(type => type.IsGenericType && type.GetGenericTypeDefinition() == contract &&
                type.GetGenericArguments().SequenceEqual(repositoryType.GetGenericArguments())))
            throw new ArgumentException($"The adapter must be a concrete open generic type implementing {contract} with entity and key parameters in that order.", nameof(repositoryType));
    }
}
