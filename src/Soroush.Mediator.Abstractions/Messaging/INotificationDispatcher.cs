namespace Soroush.Mediator.Abstractions;

/// <summary>Dispatches notifications through a configured transport or local delivery mechanism.</summary>
public interface INotificationDispatcher
{
    /// <summary>Dispatches a notification. Implementations define their delivery and error semantics.</summary>
    Task Publish(INotification notification, CancellationToken cancellationToken = default);
}
