namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a published notification.</summary>
public interface INotificationSubscriber<in TNotification> where TNotification : INotification
{
    /// <summary>Handles the notification.</summary>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
