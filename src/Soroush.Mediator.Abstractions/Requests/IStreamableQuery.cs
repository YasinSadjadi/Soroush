namespace Soroush.Mediator.Abstractions;

/// <summary>A query that returns a stream of responses.</summary>
public interface IStreamableQuery<out TResponse> : IStreamRequest<TResponse> { }
