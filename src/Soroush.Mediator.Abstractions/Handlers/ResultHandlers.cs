namespace Soroush.Mediator.Abstractions;

/// <summary>Command handler with access, validation and execution stages.</summary>
public abstract class ResultCommandHandler<TCommand, TValue> : ICommandHandler<TCommand, HandlerResult<TValue>>
    where TCommand : ICommand<HandlerResult<TValue>>
{
    /// <summary>Checks access; succeeds by default.</summary>
    protected virtual Task<HandlerResult> HasAccess(TCommand request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Validates the command; succeeds by default.</summary>
    protected virtual Task<HandlerResult> Validate(TCommand request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Executes a valid, authorized command.</summary>
    protected abstract Task<HandlerResult<TValue>> Execute(TCommand request, CancellationToken cancellationToken);
    /// <inheritdoc />
    public async Task<HandlerResult<TValue>> Handle(TCommand request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult access;
        using (var stage = MediatorObservation.BeginStage("HasAccess"))
        {
            try
            {
                using var active = stage?.Activate();
                access = await HasAccess(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(access);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        if (!access.IsSuccess) return HandlerResult<TValue>.Failure(HandlerResultCode.HasAccess, access.Message);
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult validation;
        using (var stage = MediatorObservation.BeginStage("Validation"))
        {
            try
            {
                using var active = stage?.Activate();
                validation = await Validate(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(validation);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        if (!validation.IsSuccess) return HandlerResult<TValue>.Failure(HandlerResultCode.Validation, validation.Message);
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult<TValue> result;
        using (var stage = MediatorObservation.BeginStage("Execution"))
        {
            try
            {
                using var active = stage?.Activate();
                result = await Execute(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(result);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        return result.IsSuccess ? result : HandlerResult<TValue>.Failure(HandlerResultCode.Execution, result.Message);
    }
}

/// <summary>Query handler with access and execution stages; queries have no validation stage.</summary>
public abstract class ResultQueryHandler<TQuery, TValue> : IQueryHandler<TQuery, HandlerResult<TValue>>
    where TQuery : IQuery<HandlerResult<TValue>>
{
    /// <summary>Checks access; succeeds by default.</summary>
    protected virtual Task<HandlerResult> HasAccess(TQuery request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Executes an authorized query.</summary>
    protected abstract Task<HandlerResult<TValue>> Execute(TQuery request, CancellationToken cancellationToken);
    /// <inheritdoc />
    public async Task<HandlerResult<TValue>> Handle(TQuery request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult access;
        using (var stage = MediatorObservation.BeginStage("HasAccess"))
        {
            try
            {
                using var active = stage?.Activate();
                access = await HasAccess(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(access);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        if (!access.IsSuccess) return HandlerResult<TValue>.Failure(HandlerResultCode.HasAccess, access.Message);
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult<TValue> result;
        using (var stage = MediatorObservation.BeginStage("Execution"))
        {
            try
            {
                using var active = stage?.Activate();
                result = await Execute(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(result);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        return result.IsSuccess ? result : HandlerResult<TValue>.Failure(HandlerResultCode.Execution, result.Message);
    }
}

/// <summary>Command handler for operations returning a status without a value.</summary>
public abstract class ResultCommandHandler<TCommand> : ICommandHandler<TCommand, HandlerResult>
    where TCommand : ICommand<HandlerResult>
{
    /// <summary>Checks access; succeeds by default.</summary>
    protected virtual Task<HandlerResult> HasAccess(TCommand request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Validates the command; succeeds by default.</summary>
    protected virtual Task<HandlerResult> Validate(TCommand request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Executes a valid, authorized command.</summary>
    protected abstract Task<HandlerResult> Execute(TCommand request, CancellationToken cancellationToken);
    /// <inheritdoc />
    public async Task<HandlerResult> Handle(TCommand request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult access;
        using (var stage = MediatorObservation.BeginStage("HasAccess"))
        {
            try
            {
                using var active = stage?.Activate();
                access = await HasAccess(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(access);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        if (!access.IsSuccess) return access.At(HandlerResultCode.HasAccess);
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult validation;
        using (var stage = MediatorObservation.BeginStage("Validation"))
        {
            try
            {
                using var active = stage?.Activate();
                validation = await Validate(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(validation);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        if (!validation.IsSuccess) return validation.At(HandlerResultCode.Validation);
        cancellationToken.ThrowIfCancellationRequested();
        HandlerResult result;
        using (var stage = MediatorObservation.BeginStage("Execution"))
        {
            try
            {
                using var active = stage?.Activate();
                result = await Execute(request, cancellationToken).ConfigureAwait(false);
                stage?.Complete(result);
            }
            catch (Exception ex) { stage?.Fail(ex, cancellationToken); throw; }
        }
        return result.At(HandlerResultCode.Execution);
    }
}
