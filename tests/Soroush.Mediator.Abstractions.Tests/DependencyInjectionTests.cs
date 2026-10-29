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
}

