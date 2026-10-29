namespace Soroush.Mediator.Abstractions;

/// <summary>Publishes notifications to subscribers.</summary>
public interface INotificationPublisher
{
    /// <summary>Publishes a notification using its concrete runtime type.</summary>
    Task Publish(INotification notification, CancellationToken cancellationToken = default);
}
