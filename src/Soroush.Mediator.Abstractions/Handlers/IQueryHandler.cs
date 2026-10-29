namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a query and returns its response.</summary>
public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse> { }

