using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Registers the query adapter and concrete application handlers derived from the base query handler.</summary>
public static class QueryServiceCollectionExtensions
{
    /// <summary>Registers the shared scoped query adapter, mediator, and concrete handlers in the supplied assemblies.</summary>
    /// <remarks>Supply the assemblies containing handler classes. Abstract and open generic handlers are skipped.
    /// Existing query adapter registrations are preserved.</remarks>
    public static IServiceCollection AddSoroushQueries<TContext>(this IServiceCollection services, params Assembly[] assemblies)
        where TContext : class, IQueryContext
    {
        services.AddSoroushQueries(assemblies);
        services.TryAddScoped<IQueryContext, TContext>();
        return services;
    }

    /// <summary>Registers mediator services and concrete handlers using an already registered IQueryContext.</summary>
    public static IServiceCollection AddSoroushQueries(this IServiceCollection services, params Assembly[] assemblies)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (assemblies.Any(assembly => assembly is null))
            throw new ArgumentException("Assemblies cannot contain null.", nameof(assemblies));

        return services.AddSoroushMediator(assemblies);
    }
}
