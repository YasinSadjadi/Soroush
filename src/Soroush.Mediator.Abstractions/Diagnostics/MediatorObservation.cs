using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Soroush.Mediator.Abstractions;

/// <summary>The final outcome of an observed operation, independent of business result codes.</summary>
public enum MediatorOutcome
{
    /// <summary>The operation succeeded.</summary>
    Succeeded,
    /// <summary>Access or validation rejected the operation.</summary>
    Rejected,
    /// <summary>The operation returned a failure.</summary>
    Failed,
    /// <summary>A related cancellation token was cancelled.</summary>
    Cancelled,
    /// <summary>A stream was disposed before completion.</summary>
    Abandoned,
    /// <summary>An unexpected exception escaped.</summary>
    Faulted
}

/// <summary>Optionally classifies application-specific responses. Implementations must not retain responses.</summary>
public interface IMediatorOutcomeClassifier
{
    /// <summary>Returns a custom outcome, or null to use the default result classification.</summary>
    MediatorOutcome? Classify(object? response);
}

/// <summary>Creates isolated observations. No request or response payload is passed to this observer.</summary>
public interface IMediatorObserver
{
    /// <summary>Starts an operation. Returning null disables observation of this operation.</summary>
    IMediatorOperationObservation? StartOperation(MediatorOperation operation);
}

/// <summary>A shared observer that disables operation allocation and signal production.</summary>
public sealed class NullMediatorObserver : IMediatorObserver
{
    private NullMediatorObserver() { }
    /// <summary>The shared disabled observer.</summary>
    public static NullMediatorObserver Instance { get; } = new();
    /// <inheritdoc />
    public IMediatorOperationObservation? StartOperation(MediatorOperation operation) => null;
}

/// <summary>Receives metadata for one operation. All callbacks are protected from affecting application execution.</summary>
public interface IMediatorOperationObservation : IDisposable
{
    /// <summary>Activates signal context and returns a lease that restores the previous context.</summary>
    IDisposable? Activate();
    /// <summary>Starts a stage.</summary>
    IMediatorStageObservation? StartStage(MediatorStage stage);
    /// <summary>Records delivery of one stream item.</summary>
    void ItemProduced();
    /// <summary>Records the final operation metadata exactly once locally.</summary>
    void Complete();
}

/// <summary>Receives metadata for a single handler stage.</summary>
public interface IMediatorStageObservation : IDisposable
{
    /// <summary>Activates stage context for a producer call.</summary>
    IDisposable? Activate();
    /// <summary>Records final stage metadata.</summary>
    void Complete();
}

/// <summary>Provides an async-flow-local bridge between runtime operations and base handlers.</summary>
public static class MediatorObservation
{
    private static readonly AsyncLocal<MediatorOperation?> current = new();
    private static long failureCount;
    /// <summary>The active operation, or null when diagnostics are disabled.</summary>
    public static MediatorOperation? Current => current.Value;
    /// <summary>The process-wide number of suppressed telemetry callback failures.</summary>
    public static long FailureCount => Interlocked.Read(ref failureCount);
    /// <summary>Reports a suppressed telemetry failure through a rate-limited, payload-free EventSource.</summary>
    public static void ReportDiagnosticFailure(string reason, Exception exception) => ReportFailure(reason, exception);

    /// <summary>Starts an observation. Direct handler/dispatcher callers can activate this operation explicitly.</summary>
    public static MediatorOperation? BeginOperation(IMediatorObserver? observer, Type requestType, string kind,
        IMediatorOutcomeClassifier? classifier = null)
    {
        if (observer is null || ReferenceEquals(observer, NullMediatorObserver.Instance)) return null;
        if (requestType is null) throw new ArgumentNullException(nameof(requestType));
        var operation = new MediatorOperation(requestType, kind, Current, classifier);
        try
        {
            var observation = observer.StartOperation(operation);
            if (observation is null) return null;
            operation.Attach(observation);
            return operation;
        }
        catch (Exception ex) { ReportFailure("operation.start", ex); return null; }
    }

    /// <summary>Starts a stage only when an operation is active.</summary>
    public static MediatorStage? BeginStage(string name, Type? handlerType = null) =>
        Current?.StartStage(name, handlerType);

    internal static IDisposable Activate(MediatorOperation operation, Func<IDisposable?>? activate)
    {
        var previous = current.Value;
        current.Value = operation;
        IDisposable? lease = null;
        try { lease = activate?.Invoke(); }
        catch (Exception ex) { ReportFailure("activate", ex); }
        return new Activation(previous, lease);
    }

    internal static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception ex) { ReportFailure("dispose", ex); }
    }

    internal static void ReportFailure(string reason, Exception exception)
    {
        Interlocked.Increment(ref failureCount);
        try { SelfDiagnostics.Log.Failure(reason, exception.GetType().FullName ?? "Exception"); }
        catch { /* A diagnostic sink must never recursively report its own failure. */ }
    }

    internal static bool IsCancellation(Exception exception, CancellationToken token) =>
        exception is OperationCanceledException &&
        token.IsCancellationRequested;

    internal static TimeSpan Elapsed(long start) =>
        TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency);

    private sealed class Activation(MediatorOperation? previous, IDisposable? lease) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { SafeDispose(lease); }
            finally { current.Value = previous; }
        }
    }

    [EventSource(Name = "Soroush-Mediator-Diagnostics")]
    private sealed class SelfDiagnostics : EventSource
    {
        public static readonly SelfDiagnostics Log = new();
        private long last;
        [Event(1990, Level = EventLevel.Warning)]
        public void Failure(string reason, string exceptionType)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref last);
            if (now - previous < Stopwatch.Frequency * 5 || Interlocked.CompareExchange(ref last, now, previous) != previous) return;
            WriteEvent(1990, reason, exceptionType);
        }
    }
}

/// <summary>Metadata and lifetime for one operation. Does not retain application payloads.</summary>
public sealed class MediatorOperation : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly IMediatorOutcomeClassifier? classifier;
    private IMediatorOperationObservation? observation;
    private int completed;
    private int disposed;
    internal MediatorOperation(Type requestType, string kind, MediatorOperation? parent, IMediatorOutcomeClassifier? classifier)
    {
        RequestType = requestType;
        Kind = kind is "command" or "query" or "stream" or "notification" ? kind : "request";
        Parent = parent;
        this.classifier = classifier;
        OperationId = Guid.NewGuid().ToString("N");
    }
    /// <summary>A correlation identifier available without a tracing listener.</summary>
    public string OperationId { get; }
    /// <summary>The enclosing mediator operation, independent of HTTP trace roots.</summary>
    public MediatorOperation? Parent { get; }
    /// <summary>The concrete request type.</summary>
    public Type RequestType { get; }
    /// <summary>Request, command, query, stream or notification.</summary>
    public string Kind { get; }
    /// <summary>The resolved local handler type, when one actually ran.</summary>
    public Type? HandlerType { get; private set; }
    /// <summary>The final outcome.</summary>
    public MediatorOutcome Outcome { get; private set; } = MediatorOutcome.Succeeded;
    /// <summary>The returned failure stage code, if any.</summary>
    public HandlerResultCode? ResultCode { get; private set; }
    /// <summary>A returned failure replaced by a cleanup failure, if any.</summary>
    public HandlerResultCode? PrimaryResultCode { get; private set; }
    /// <summary>The first failure stage, or a stream construction phase.</summary>
    public string? FailureStage { get; private set; }
    /// <summary>The escaping exception. It is never copied into summary message text.</summary>
    public Exception? Exception { get; private set; }
    /// <summary>An earlier exception replaced during producer cleanup, if any.</summary>
    public Exception? PrimaryException { get; private set; }
    /// <summary>Total elapsed operation time.</summary>
    public TimeSpan Duration { get; private set; }
    /// <summary>Time spent inside stream producer calls, excluding consumer pauses.</summary>
    public TimeSpan ProducerDuration { get; private set; }
    /// <summary>Number of stream items delivered.</summary>
    public long ItemCount { get; private set; }
    /// <summary>Time to the first delivered item, or null for empty streams.</summary>
    public TimeSpan? TimeToFirstItem { get; private set; }
    internal void Attach(IMediatorOperationObservation value) => observation = value;
    /// <summary>Activates this operation and restores the caller context when disposed.</summary>
    public IDisposable Activate() => MediatorObservation.Activate(this, observation is null ? null : observation.Activate);
    /// <summary>Records the actual handler without capturing its instance.</summary>
    public void SetHandler(Type handlerType) => HandlerType = handlerType;
    /// <summary>Starts a stage attached to this operation.</summary>
    public MediatorStage StartStage(string name, Type? handlerType = null)
    {
        var stage = new MediatorStage(this, name, handlerType);
        try { stage.Attach(observation?.StartStage(stage)); }
        catch (Exception ex) { MediatorObservation.ReportFailure("stage.start", ex); }
        return stage;
    }
    /// <summary>Classifies a response without retaining its payload.</summary>
    public void RecordResult(object? result)
    {
        MediatorOutcome? custom = null;
        try { custom = classifier?.Classify(result); }
        catch (Exception ex) { MediatorObservation.ReportFailure("classify", ex); }
        if (custom.HasValue && !Enum.IsDefined(typeof(MediatorOutcome), custom.Value))
        {
            MediatorObservation.ReportFailure("classify", new ArgumentOutOfRangeException(nameof(custom)));
            custom = null;
        }
        Exception = null;
        PrimaryException = null;
        PrimaryResultCode = null;
        if (result is HandlerResult failure && !failure.IsSuccess)
        {
            ResultCode = failure.Code;
            FailureStage = failure.Code.ToString();
            Outcome = custom ?? (failure.Code == HandlerResultCode.HasAccess || failure.Code == HandlerResultCode.Validation
                ? MediatorOutcome.Rejected : MediatorOutcome.Failed);
        }
        else
        {
            Outcome = custom ?? MediatorOutcome.Succeeded;
            ResultCode = null;
            FailureStage = null;
        }
    }
    /// <summary>Records an escaping exception and preserves the earliest known failure stage.</summary>
    public void RecordException(Exception exception, CancellationToken token = default, string? stage = null)
    {
        var alreadyCancelled = ReferenceEquals(Exception, exception) && Outcome == MediatorOutcome.Cancelled;
        var changedException = Exception != null && !ReferenceEquals(Exception, exception);
        if (changedException) PrimaryException ??= Exception;
        Exception = exception;
        if (exception is HandlerStageException cleanup)
        {
            if (ResultCode.HasValue && ResultCode != cleanup.Code) PrimaryResultCode ??= ResultCode;
            ResultCode = cleanup.Code;
            FailureStage = cleanup.Code.ToString();
        }
        else if (stage != null && (FailureStage is null || changedException)) FailureStage = stage;
        Outcome = alreadyCancelled || MediatorObservation.IsCancellation(exception, token) ? MediatorOutcome.Cancelled : MediatorOutcome.Faulted;
    }
    /// <summary>Records one delivered item and classifies failed result items.</summary>
    public void RecordItem(object? item)
    {
        ItemCount++;
        if (ItemCount == 1) TimeToFirstItem = MediatorObservation.Elapsed(started);
        if (Outcome == MediatorOutcome.Succeeded) RecordResult(item);
        try { observation?.ItemProduced(); }
        catch (Exception ex) { MediatorObservation.ReportFailure("stream.item", ex); }
    }
    /// <summary>Adds time spent inside MoveNextAsync or DisposeAsync.</summary>
    public void AddProducerTime(TimeSpan duration) => ProducerDuration += duration;
    /// <summary>Finishes this operation once. An observed failure takes precedence over early disposal.</summary>
    public void Complete(bool abandoned = false)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        if (abandoned && Outcome == MediatorOutcome.Succeeded) Outcome = MediatorOutcome.Abandoned;
        Duration = MediatorObservation.Elapsed(started);
        try { observation?.Complete(); }
        catch (Exception ex) { MediatorObservation.ReportFailure("operation.complete", ex); }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Complete(abandoned: true);
        MediatorObservation.SafeDispose(observation);
    }
}

/// <summary>Metadata and lifetime for one stage or notification subscriber.</summary>
public sealed class MediatorStage : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    private IMediatorStageObservation? observation;
    private int completed;
    private int disposed;
    internal MediatorStage(MediatorOperation operation, string name, Type? handlerType)
    {
        Operation = operation; Name = name; HandlerType = handlerType;
    }
    /// <summary>The owning operation.</summary>
    public MediatorOperation Operation { get; }
    /// <summary>The fixed stage name.</summary>
    public string Name { get; }
    /// <summary>The concrete subscriber type, if this is a subscriber stage.</summary>
    public Type? HandlerType { get; }
    /// <summary>The outcome of this stage.</summary>
    public MediatorOutcome Outcome { get; private set; } = MediatorOutcome.Succeeded;
    /// <summary>The exception from this stage, if any.</summary>
    public Exception? Exception { get; private set; }
    /// <summary>The normalized stage failure code, when a business result was returned.</summary>
    public HandlerResultCode? ResultCode { get; private set; }
    /// <summary>Stage duration, including consumer pauses for a streaming execution stage.</summary>
    public TimeSpan Duration { get; private set; }
    internal void Attach(IMediatorStageObservation? value) => observation = value;
    /// <summary>Activates this stage without leaking its context between stream items.</summary>
    public IDisposable Activate() => MediatorObservation.Activate(Operation, observation is null ? null : observation.Activate);
    /// <summary>Finishes a stage and records its result without retaining the result object.</summary>
    public void Complete(HandlerResult? result = null)
    {
        if (result != null && !result.IsSuccess)
        {
            ResultCode = Enum.TryParse<HandlerResultCode>(Name, out var code) ? code : result.Code;
            Outcome = ResultCode == HandlerResultCode.HasAccess || ResultCode == HandlerResultCode.Validation
                ? MediatorOutcome.Rejected : MediatorOutcome.Failed;
        }
        Finish();
    }
    /// <summary>Records and re-exposes stage information without changing exception semantics.</summary>
    public void Fail(Exception exception, CancellationToken token = default)
    {
        Exception = exception;
        Outcome = MediatorObservation.IsCancellation(exception, token) ? MediatorOutcome.Cancelled : MediatorOutcome.Faulted;
        Operation.RecordException(exception, token, Name);
        Finish();
    }
    private void Finish()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        Duration = MediatorObservation.Elapsed(started);
        try { observation?.Complete(); }
        catch (Exception ex) { MediatorObservation.ReportFailure("stage.complete", ex); }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Finish();
        MediatorObservation.SafeDispose(observation);
    }
}
