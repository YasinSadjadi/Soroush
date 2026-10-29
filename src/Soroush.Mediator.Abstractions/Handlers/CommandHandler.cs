namespace Soroush.Mediator.Abstractions;

/// <summary>Base class for handlers of commands without a response.</summary>
public abstract class CommandHandler<TCommand> : ICommandHandler<TCommand> where TCommand : ICommand
{
    /// <inheritdoc />
    public abstract Task Handle(TCommand request, CancellationToken cancellationToken);
}

/// <summary>Base class for handlers of commands with a response.</summary>
public abstract class CommandHandler<TCommand, TResponse> : ICommandHandler<TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    /// <inheritdoc />
    public abstract Task<TResponse> Handle(TCommand request, CancellationToken cancellationToken);
}
