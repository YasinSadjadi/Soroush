namespace Soroush.Mediator.Abstractions;

/// <summary>Sends requests to their handlers.</summary>
public interface ISender
{
    /// <summary>Sends a request and returns its response.</summary>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request without a response.</summary>
    Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest;

    /// <summary>Sends a request whose response type is known at runtime.</summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request and returns its response stream.</summary>
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>Sends a request whose stream item type is known at runtime.</summary>
    IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default);
}
