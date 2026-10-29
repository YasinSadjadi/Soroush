namespace Soroush.Mediator.Abstractions;

/// <summary>A query that returns a response.</summary>
public interface IQuery<out TResponse> : IRequest<TResponse> { }

