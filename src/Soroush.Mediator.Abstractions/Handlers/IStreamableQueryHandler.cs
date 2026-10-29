namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a query that returns a stream.</summary>
public interface IStreamableQueryHandler<in TQuery, out TResponse> : IStreamRequestHandler<TQuery, TResponse>
    where TQuery : IStreamableQuery<TResponse> { }
