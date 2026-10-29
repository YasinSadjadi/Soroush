namespace Soroush.Mediator.Abstractions;

/// <summary>Handles a command without a response.</summary>
public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand> where TCommand : ICommand { }

/// <summary>Handles a command and returns its response.</summary>
public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse> { }
