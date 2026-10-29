using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Registers mediator services and handlers.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers scoped mediator services and handlers from the supplied assemblies.</summary>
    public static IServiceCollection AddSoroushMediator(this IServiceCollection services, params Assembly[] assemblies)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (assemblies is null) throw new ArgumentNullException(nameof(assemblies));
        if (assemblies.Any(assembly => assembly is null))
            throw new ArgumentException("Assemblies cannot contain null.", nameof(assemblies));
        services.TryAddScoped<ISender, Mediator>();
        services.TryAddScoped<IRequestDispatcher, LocalRequestDispatcher>();
        services.TryAddScoped<INotificationPublisher, NotificationPublisher>();
        services.TryAddScoped<INotificationDispatcher, LocalNotificationDispatcher>();
        foreach (var implementation in assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters))
        {
            foreach (var contract in implementation.GetInterfaces().Where(IsHandlerContract))
                services.TryAddEnumerable(ServiceDescriptor.Scoped(contract, implementation));
        }
        return services;
    }

    /// <summary>Replaces the request dispatcher with a scoped implementation.</summary>
    public static IServiceCollection UseRequestDispatcher<TDispatcher>(this IServiceCollection services) where TDispatcher : class, IRequestDispatcher
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.Replace(ServiceDescriptor.Scoped<IRequestDispatcher, TDispatcher>());
        return services;
    }

    /// <summary>Replaces the notification dispatcher with a scoped implementation.</summary>
    public static IServiceCollection UseNotificationDispatcher<TDispatcher>(this IServiceCollection services) where TDispatcher : class, INotificationDispatcher
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        services.Replace(ServiceDescriptor.Scoped<INotificationDispatcher, TDispatcher>());
        return services;
    }
    private static bool IsHandlerContract(Type type)
    {
        if (!type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IRequestHandler<>) || definition == typeof(IRequestHandler<,>) ||
               definition == typeof(IStreamRequestHandler<,>) || definition == typeof(INotificationSubscriber<>) ||
               definition == typeof(IPipelineBehavior<,>);
    }
}


