using System.Reflection;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Publishes notifications to subscribers resolved from the supplied service provider.</summary>
public sealed class LocalNotificationDispatcher : INotificationDispatcher
{
    private readonly IServiceProvider services;

    /// <summary>Creates a publisher using the supplied service provider and its scope.</summary>
    public LocalNotificationDispatcher(IServiceProvider services) => this.services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        if (notification is null) throw new ArgumentNullException(nameof(notification));
        var method = typeof(LocalNotificationDispatcher).GetMethod(nameof(PublishCore), BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.MakeGenericMethod(notification.GetType()).Invoke(this, new object[] { notification, cancellationToken })!;
    }

    private async Task PublishCore<TNotification>(TNotification notification, CancellationToken cancellationToken) where TNotification : INotification
    {
        cancellationToken.ThrowIfCancellationRequested();
        var subscribers = services.GetService(typeof(IEnumerable<INotificationSubscriber<TNotification>>)) as IEnumerable<INotificationSubscriber<TNotification>>;
        if (subscribers is null) return;
        foreach (var subscriber in subscribers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscriber.Handle(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}

