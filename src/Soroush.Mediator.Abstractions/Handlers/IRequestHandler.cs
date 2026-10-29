namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a request without a response.</summary>
public interface IRequestHandler<in TRequest> where TRequest : IRequest
{
    /// <summary>Handles the request.</summary>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handles a request and returns its response.</summary>
public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    /// <summary>Handles the request.</summary>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
