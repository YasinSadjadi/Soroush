using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator;

/// <summary>Routes sender operations through the configured dispatcher.</summary>
public sealed class Mediator : ISender
{
    private readonly IRequestDispatcher dispatcher;
    /// <summary>Creates a sender using the supplied dispatcher.</summary>
    public Mediator(IRequestDispatcher dispatcher) => this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => dispatcher.Send(request, cancellationToken);
    /// <inheritdoc />
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => dispatcher.Send(request, cancellationToken);
    /// <inheritdoc />
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => dispatcher.Send(request, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => dispatcher.CreateStream(request, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => dispatcher.CreateStream(request, cancellationToken);
}
