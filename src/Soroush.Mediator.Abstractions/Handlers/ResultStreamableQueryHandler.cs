using System.Runtime.CompilerServices;

namespace Soroush.Mediator.Abstractions;

/// <summary>Streaming query handler with observed access, preparation, execution and cleanup stages.</summary>
/// <remarks>Cleanup runs once after access succeeds, including early disposal and cancellation.
/// Cleanup uses an uncancelled token. Cleanup failures preserve an earlier producer exception.</remarks>
public abstract class ResultStreamableQueryHandler<TQuery, TValue> : IStreamableQueryHandler<TQuery, HandlerResult<TValue>>
    where TQuery : IStreamableQuery<HandlerResult<TValue>>
{
    /// <summary>Checks access; succeeds by default.</summary>
    protected virtual Task<HandlerResult> HasAccess(TQuery request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Prepares the stream; succeeds by default.</summary>
    protected virtual Task<HandlerResult> PreProcess(TQuery request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());
    /// <summary>Produces results for the authorized stream.</summary>
    protected abstract IAsyncEnumerable<HandlerResult<TValue>> Execute(TQuery request, CancellationToken cancellationToken);
    /// <summary>Cleans up using an uncancelled token.</summary>
    protected virtual Task<HandlerResult> PostProcess(TQuery request, CancellationToken cancellationToken) => Task.FromResult(HandlerResult.Success());

    /// <inheritdoc />
    public async IAsyncEnumerable<HandlerResult<TValue>> Handle(TQuery request, [EnumeratorCancellation] CancellationToken cancellationToken)
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
        if (!access.IsSuccess)
        {
            yield return HandlerResult<TValue>.Failure(HandlerResultCode.HasAccess, access.Message);
            yield break;
        }
        Exception? primary = null;
        try
        {
            HandlerResult prepared;
            using (var stage = MediatorObservation.BeginStage("PreProcess"))
            {
                try
                {
                    using var active = stage?.Activate();
                    cancellationToken.ThrowIfCancellationRequested();
                    prepared = await PreProcess(request, cancellationToken).ConfigureAwait(false);
                    stage?.Complete(prepared);
                }
                catch (Exception ex) { primary = ex; stage?.Fail(ex, cancellationToken); throw; }
            }
            if (!prepared.IsSuccess)
            {
                yield return HandlerResult<TValue>.Failure(HandlerResultCode.PreProcess, prepared.Message);
                yield break;
            }
            using var execution = MediatorObservation.BeginStage("Execution");
            IAsyncEnumerator<HandlerResult<TValue>>? iterator = null;
            HandlerResult<TValue>? failure = null;
            try
            {
                try
                {
                    using var active = execution?.Activate();
                    iterator = Execute(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                }
                catch (Exception ex) { primary = ex; execution?.Fail(ex, cancellationToken); throw; }
                while (true)
                {
                    bool more;
                    HandlerResult<TValue>? item = null;
                    try
                    {
                        using var active = execution?.Activate();
                        more = await iterator.MoveNextAsync().ConfigureAwait(false);
                        if (more)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            item = iterator.Current;
                        }
                    }
                    catch (Exception ex) { primary = ex; execution?.Fail(ex, cancellationToken); throw; }
                    if (!more) break;
                    if (!item!.IsSuccess) failure = HandlerResult<TValue>.Failure(HandlerResultCode.Execution, item.Message);
                    yield return failure ?? item;
                    if (failure != null) break;
                }
            }
            finally
            {
                if (iterator != null)
                {
                    try
                    {
                        using var active = execution?.Activate();
                        await iterator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) { primary ??= ex; execution?.Fail(ex, cancellationToken); throw; }
                }
                execution?.Complete(failure);
            }
        }
        finally
        {
            using var stage = MediatorObservation.BeginStage("PostProcess");
            HandlerResult cleanup;
            try
            {
                using var active = stage?.Activate();
                cleanup = await PostProcess(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var wrapped = new HandlerStageException(HandlerResultCode.PostProcess, "Stream cleanup failed.", ex, primary);
                stage?.Fail(wrapped);
                throw wrapped;
            }
            if (!cleanup.IsSuccess)
            {
                var wrapped = new HandlerStageException(HandlerResultCode.PostProcess, cleanup.Message ?? "Stream cleanup failed.", null, primary);
                stage?.Fail(wrapped);
                throw wrapped;
            }
            stage?.Complete(cleanup);
        }
    }
}
