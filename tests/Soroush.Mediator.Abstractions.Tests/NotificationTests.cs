using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Abstractions.Tests;

public class NotificationTests
{
    private sealed class Event : INotification { }
    private sealed class Subscriber : INotificationSubscriber<Event>
    {
        private readonly Func<Event, CancellationToken, Task> handle;
        public Subscriber(Func<Event, CancellationToken, Task> handle) => this.handle = handle;
        public Task Handle(Event notification, CancellationToken cancellationToken) => handle(notification, cancellationToken);
    }
    private sealed class Services : IServiceProvider
    {
        private readonly INotificationSubscriber<Event>[] subscribers;
        public Services(params INotificationSubscriber<Event>[] subscribers) => this.subscribers = subscribers;
        public object? GetService(Type type) => type == typeof(IEnumerable<INotificationSubscriber<Event>>) ? subscribers : null;
    }

    private sealed class RecordingDispatcher : INotificationDispatcher
    {
        public INotification? Notification { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource<object?> Completion { get; } = new();
        public Task Publish(INotification notification, CancellationToken cancellationToken = default)
        {
            Notification = notification;
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    [Fact]
    public async Task PublisherDelegatesToConfiguredDispatcherAndPropagatesFailure()
    {
        var dispatcher = new RecordingDispatcher();
        INotificationPublisher publisher = new NotificationPublisher(dispatcher);
        var notification = new Event();
        using var source = new CancellationTokenSource();
        var result = publisher.Publish(notification, source.Token);
        Assert.Same(notification, dispatcher.Notification);
        Assert.Equal(source.Token, dispatcher.Token);
        Assert.False(result.IsCompleted);
        dispatcher.Completion.SetException(new InvalidOperationException("transport failure"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        Assert.Equal("transport failure", error.Message);
    }

    [Fact]
    public void PublisherRejectsNullDispatcher() => Assert.Throws<ArgumentNullException>(() => new NotificationPublisher((INotificationDispatcher)null!));
    [Fact]
    public async Task PublishesToAllSubscribersInOrderUsingRuntimeType()
    {
        var calls = new List<int>();
        var notification = new Event();
        using var source = new CancellationTokenSource();
        var publisher = new NotificationPublisher(new Services(
            new Subscriber((e, ct) => { Assert.Same(notification, e); Assert.Equal(source.Token, ct); calls.Add(1); return Task.CompletedTask; }),
            new Subscriber((e, ct) => { calls.Add(2); return Task.CompletedTask; })));
        INotification typedAsInterface = notification;
        await publisher.Publish(typedAsInterface, source.Token);
        Assert.Equal(new[] { 1, 2 }, calls);
    }

    [Fact]
    public async Task NoSubscribersIsSuccessful()
    {
        await new LocalNotificationPublisher(new Services()).Publish(new Event());
    }

    [Fact]
    public async Task FailureStopsSubsequentSubscribers()
    {
        var called = false;
        var publisher = new NotificationPublisher(new LocalNotificationDispatcher(new Services(
            new Subscriber((e, ct) => Task.FromException(new InvalidOperationException("failure"))),
            new Subscriber((e, ct) => { called = true; return Task.CompletedTask; }))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.Publish(new Event()));
        Assert.Equal("failure", error.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationStopsSubsequentSubscribers()
    {
        using var source = new CancellationTokenSource();
        var called = false;
        var publisher = new NotificationPublisher(new LocalNotificationDispatcher(new Services(
            new Subscriber((e, ct) => { source.Cancel(); return Task.CompletedTask; }),
            new Subscriber((e, ct) => { called = true; return Task.CompletedTask; }))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.Publish(new Event(), source.Token));
        Assert.False(called);
    }

    [Fact]
    public void NullNotificationIsRejected() => Assert.Throws<ArgumentNullException>(() => { _ = new LocalNotificationPublisher(new Services()).Publish(null!); });
}



