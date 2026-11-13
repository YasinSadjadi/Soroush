using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Publishes notifications locally using the supplied service provider.</summary>
public sealed class LocalNotificationPublisher : INotificationPublisher
{
    private readonly NotificationPublisher publisher;

    /// <summary>Creates a local publisher using the supplied service provider and its scope.</summary>
    public LocalNotificationPublisher(IServiceProvider services) => publisher = new NotificationPublisher(new LocalNotificationDispatcher(services));
    /// <summary>Creates a local publisher with explicit observation.</summary>
    public LocalNotificationPublisher(IServiceProvider services, IMediatorObserver? observer) =>
        publisher = new NotificationPublisher(new LocalNotificationDispatcher(services), observer);

    /// <inheritdoc />
    public Task Publish(INotification notification, CancellationToken cancellationToken = default) => publisher.Publish(notification, cancellationToken);
}
