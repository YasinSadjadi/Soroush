using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Publishes notifications locally using the supplied service provider.</summary>
public sealed class LocalNotificationPublisher : INotificationPublisher
{
    private readonly LocalNotificationDispatcher dispatcher;

    /// <summary>Creates a local publisher using the supplied service provider and its scope.</summary>
    public LocalNotificationPublisher(IServiceProvider services) => dispatcher = new LocalNotificationDispatcher(services);

    /// <inheritdoc />
    public Task Publish(INotification notification, CancellationToken cancellationToken = default) => dispatcher.Publish(notification, cancellationToken);
}
