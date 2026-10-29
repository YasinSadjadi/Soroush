using Microsoft.Extensions.DependencyInjection;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Abstractions.Tests;

public class DependencyInjectionTests
{
    public sealed class Ping : Command<string> { }
    public sealed class PingHandler : CommandHandler<Ping, string>
    {
        public override Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("pong");
    }
    public sealed class AlternativeDispatcher : IRequestDispatcher
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => Task.FromResult((TResponse)(object)"remote");
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>("remote");
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    public sealed class Notice : INotification { }
    public sealed class AlternativeNotificationDispatcher : INotificationDispatcher
    {
        public bool Published { get; private set; }
        public Task Publish(INotification notification, CancellationToken cancellationToken = default)
        {
            Published = true;
            return Task.CompletedTask;
        }
    }
    [Fact]
    public async Task NotificationDispatcherDefaultsToLocalAndCanBeReplaced()
    {
        var defaults = new ServiceCollection().AddSoroushMediator();
        using (var provider = defaults.BuildServiceProvider())
        using (var scope = provider.CreateScope())
        {
            Assert.IsType<LocalNotificationDispatcher>(scope.ServiceProvider.GetRequiredService<INotificationDispatcher>());
            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().Publish(new Notice());
        }
        foreach (var before in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (before) services.UseNotificationDispatcher<AlternativeNotificationDispatcher>();
            services.AddSoroushMediator();
            if (!before) services.UseNotificationDispatcher<AlternativeNotificationDispatcher>();
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().Publish(new Notice());
            Assert.True(((AlternativeNotificationDispatcher)scope.ServiceProvider.GetRequiredService<INotificationDispatcher>()).Published);
        }
    }
    [Fact]
    public async Task RegistrationFindsHandlersAndUsesLocalDispatcher()
    {
        var services = new ServiceCollection().AddSoroushMediator(typeof(Ping).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<LocalRequestDispatcher>(scope.ServiceProvider.GetRequiredService<IRequestDispatcher>());
        Assert.Equal("pong", await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping()));
    }
    [Fact]
    public async Task CustomDispatcherCanBeSelectedBeforeOrAfterRegistration()
    {
        foreach (var before in new[] { true, false })
        {
            var services = new ServiceCollection();
            if (before) services.UseRequestDispatcher<AlternativeDispatcher>();
            services.AddSoroushMediator();
            if (!before) services.UseRequestDispatcher<AlternativeDispatcher>();
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            Assert.Equal("remote", await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ping()));
        }
    }

    [Fact]
    public void RegistrationIsIdempotentForRepeatedAssembliesAndCalls()
    {
        var assembly = typeof(Ping).Assembly;
        var services = new ServiceCollection();
        Assert.Same(services, services.AddSoroushMediator(assembly, assembly));
        services.AddSoroushMediator(assembly);
        Assert.Single(services, d => d.ServiceType == typeof(ISender));
        Assert.Single(services, d => d.ServiceType == typeof(IRequestDispatcher));
        Assert.Single(services, d => d.ServiceType == typeof(INotificationPublisher));
        Assert.Single(services, d => d.ServiceType == typeof(INotificationDispatcher));
        Assert.Single(services, d => d.ServiceType == typeof(IRequestHandler<Ping, string>));
        Assert.Single(services, d => d.ServiceType == typeof(INotificationSubscriber<Notice>));
        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<Ping, string>));
        Assert.All(services, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
    }

    [Fact]
    public void MediatorServicesAndHandlersRespectScopes()
    {
        using var provider = new ServiceCollection().AddSoroushMediator(typeof(Ping).Assembly)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        foreach (var serviceType in new[]
        {
            typeof(ISender), typeof(IRequestDispatcher), typeof(INotificationPublisher),
            typeof(INotificationDispatcher), typeof(IRequestHandler<Ping, string>),
            typeof(INotificationSubscriber<Notice>), typeof(IPipelineBehavior<Ping, string>)
        })
        {
            var instance = first.ServiceProvider.GetRequiredService(serviceType);
            Assert.Same(instance, first.ServiceProvider.GetRequiredService(serviceType));
            Assert.NotSame(instance, second.ServiceProvider.GetRequiredService(serviceType));
        }
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());
    }

    [Fact]
    public async Task ScannedSubscribersAndOpenGenericBehaviorsParticipateInDispatch()
    {
        var services = new ServiceCollection().AddSoroushMediator(typeof(Ping).Assembly);
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TrackingBehavior<,>));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider;
        Assert.Equal("pong", await scoped.GetRequiredService<ISender>().Send(new Ping()));
        Assert.True(((TrackingBehavior<Ping, string>)scoped.GetServices<IPipelineBehavior<Ping, string>>().Last()).Called);
        await scoped.GetRequiredService<INotificationPublisher>().Publish(new Notice());
        Assert.Equal(1, ((NoticeSubscriber)scoped.GetRequiredService<INotificationSubscriber<Notice>>()).Calls);
    }

    [Fact]
    public void ScanningExcludesAbstractAndOpenGenericImplementations()
    {
        var services = new ServiceCollection().AddSoroushMediator(typeof(Ping).Assembly);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType?.IsAbstract == true);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType?.ContainsGenericParameters == true);
    }

    [Fact]
    public void RegistrationRejectsNullArguments()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddSoroushMediator(null!)).ParamName);
        Assert.Equal("assemblies", Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddSoroushMediator(null!)).ParamName);
        var services = new ServiceCollection();
        Assert.Equal("assemblies", Assert.Throws<ArgumentException>(() => services.AddSoroushMediator(typeof(Ping).Assembly, null!)).ParamName);
        Assert.Empty(services);
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.UseRequestDispatcher<AlternativeDispatcher>(null!)).ParamName);
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.UseNotificationDispatcher<AlternativeNotificationDispatcher>(null!)).ParamName);
    }

    public sealed class NoticeSubscriber : INotificationSubscriber<Notice>
    {
        public int Calls { get; private set; }
        public Task Handle(Notice notification, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    public sealed class PingBehavior : IPipelineBehavior<Ping, string>
    {
        public Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    public abstract class AbstractPingHandler : IRequestHandler<Ping, string>
    {
        public abstract Task<string> Handle(Ping request, CancellationToken cancellationToken);
    }

    public sealed class TrackingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public bool Called { get; private set; }
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            Called = true;
            return next(cancellationToken);
        }
    }
}

