namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a request that returns a stream.</summary>
public interface IStreamRequestHandler<in TRequest, out TResponse> where TRequest : IStreamRequest<TResponse>
{
    /// <summary>Handles the request and returns its response stream.</summary>
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

