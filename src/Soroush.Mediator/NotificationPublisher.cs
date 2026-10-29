using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Routes notification publishing through the configured dispatcher.</summary>
public sealed class NotificationPublisher : INotificationPublisher
{
    private readonly INotificationDispatcher dispatcher;

    /// <summary>Creates a publisher using the supplied dispatcher.</summary>
    public NotificationPublisher(INotificationDispatcher dispatcher) => this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <summary>Creates a publisher using local in-memory dispatch by default, or the supplied dispatcher.</summary>
    /// <param name="services">The provider and scope used for local subscriber resolution.</param>
    /// <param name="dispatcher">An optional alternative transport dispatcher.</param>
    public NotificationPublisher(IServiceProvider services, INotificationDispatcher? dispatcher = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        this.dispatcher = dispatcher ?? new LocalNotificationDispatcher(services);
    }

    /// <inheritdoc />
    public Task Publish(INotification notification, CancellationToken cancellationToken = default) => dispatcher.Publish(notification, cancellationToken);
}

