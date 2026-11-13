using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Routes sender operations through the configured dispatcher.</summary>
public sealed class Mediator : ISender
{
    private readonly IRequestDispatcher dispatcher;
    private readonly IMediatorObserver? observer;
    private readonly IMediatorOutcomeClassifier? classifier;
    /// <summary>Creates a sender using the supplied dispatcher.</summary>
    public Mediator(IRequestDispatcher dispatcher) => this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    /// <summary>Creates a sender with optional metadata-only observation and result classification.</summary>
    public Mediator(IRequestDispatcher dispatcher, IMediatorObserver? observer, IMediatorOutcomeClassifier? classifier = null) : this(dispatcher)
    { this.observer = ReferenceEquals(observer, NullMediatorObserver.Instance) ? null : observer; this.classifier = classifier; }
    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        observer is null || request is null ? dispatcher.Send(request!, cancellationToken) :
        ObservedExecution.Send(request, observer, classifier, () => dispatcher.Send(request, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
        observer is null || request is null ? dispatcher.Send(request!, cancellationToken) :
        ObservedExecution.Send(request, ObservedExecution.Kind(request.GetType()), observer, () => dispatcher.Send(request, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
        observer is null || request is null ? dispatcher.Send(request!, cancellationToken) :
        ObservedExecution.Send(request, observer, classifier, () => dispatcher.Send(request, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        observer is null || request is null ? dispatcher.CreateStream(request!, cancellationToken) :
        ObservedExecution.Stream(request, observer, classifier, () => dispatcher.CreateStream(request, cancellationToken), cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        observer is null || request is null ? dispatcher.CreateStream(request!, cancellationToken) :
        ObservedExecution.Stream(request, observer, classifier, () => dispatcher.CreateStream(request, cancellationToken), cancellationToken);
}
