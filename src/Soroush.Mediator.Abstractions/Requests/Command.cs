namespace Soroush.Mediator.Abstractions;

/// <summary>Base class for commands without a response.</summary>
public abstract class Command : ICommand { }

/// <summary>Base class for commands with a response.</summary>
public abstract class Command<TResponse> : ICommand<TResponse> { }
