namespace Soroush.Mediator.Abstractions;

/// <summary>A command that does not return a response.</summary>
public interface ICommand : IRequest { }

/// <summary>A command that returns a response.</summary>
public interface ICommand<out TResponse> : IRequest<TResponse> { }
