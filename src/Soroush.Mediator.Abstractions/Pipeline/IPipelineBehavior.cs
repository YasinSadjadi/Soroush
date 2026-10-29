namespace Soroush.Mediator.Abstractions;

/// <summary>Wraps the next handler in a request pipeline.</summary>
public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : notnull
{
    /// <summary>Handles the request or delegates to the next pipeline component.</summary>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
