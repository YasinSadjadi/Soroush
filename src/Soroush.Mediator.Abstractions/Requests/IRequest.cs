namespace Soroush.Mediator.Abstractions;

/// <summary>A request that does not return a response.</summary>
public interface IRequest { }

/// <summary>A request that returns a response.</summary>
public interface IRequest<out TResponse> { }
