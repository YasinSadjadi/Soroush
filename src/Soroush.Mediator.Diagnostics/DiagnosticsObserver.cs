using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Soroush.Mediator.Abstractions;

namespace Soroush.Mediator.Diagnostics;

internal sealed class DiagnosticsObserver : IMediatorObserver, IDisposable
{
    private readonly SoroushDiagnosticsOptions options;
    private readonly ILogger logger;
    private readonly ActivitySource source;
    private readonly Meter meter;
    private readonly Histogram<double> duration;
    private readonly Histogram<double> stageDuration;
    private readonly Histogram<double> producerDuration;
    private readonly Histogram<double> firstItem;
    private readonly UpDownCounter<long> active;
    private readonly Counter<long> items;
    private readonly BoundedNames names;
    private readonly BoundedNames errors;
    private int disposed;

    internal DiagnosticsObserver(ILoggerFactory factory, SoroushDiagnosticsOptions options)
    {
        this.options = options;
        logger = factory.CreateLogger("Soroush.Mediator.Diagnostics");
        var version = typeof(DiagnosticsObserver).Assembly.GetName().Version?.ToString();
        source = new ActivitySource(SoroushDiagnostics.ActivitySourceName, version);
        meter = new Meter(SoroushDiagnostics.MeterName, version);
        duration = meter.CreateHistogram<double>("soroush.mediator.operation.duration", "s");
        stageDuration = meter.CreateHistogram<double>("soroush.mediator.stage.duration", "s");
        producerDuration = meter.CreateHistogram<double>("soroush.mediator.stream.producer.duration", "s");
        firstItem = meter.CreateHistogram<double>("soroush.mediator.stream.time_to_first_item", "s");
        active = meter.CreateUpDownCounter<long>("soroush.mediator.operation.active", "{operation}");
        items = meter.CreateCounter<long>("soroush.mediator.stream.items", "{item}");
        if (options.EnableMetrics)
            meter.CreateObservableCounter("soroush.mediator.diagnostics.failures", () => MediatorObservation.FailureCount, "{failure}");
        names = new BoundedNames(options.MaxMetricNames);
        errors = new BoundedNames(options.MaxMetricErrorTypes);
    }

    public IMediatorOperationObservation? StartOperation(MediatorOperation operation) =>
        !options.EnableLogging && !options.EnableTracing && !options.EnableMetrics ? null : new Operation(this, operation);

    private static string Outcome(MediatorOutcome outcome) => outcome switch
    {
        MediatorOutcome.Succeeded => "succeeded", MediatorOutcome.Rejected => "rejected",
        MediatorOutcome.Failed => "failed", MediatorOutcome.Cancelled => "cancelled",
        MediatorOutcome.Abandoned => "abandoned", _ => "faulted"
    };
    private static string TypeName(Type? type) => type?.FullName ?? type?.Name ?? "";
    private static string? Error(MediatorOutcome outcome, HandlerResultCode? code, Exception? exception) =>
        outcome == MediatorOutcome.Faulted ? exception is null ? "soroush.mediator.fault" : TypeName(exception.GetType()) :
        outcome == MediatorOutcome.Failed || outcome == MediatorOutcome.Rejected
            ? "soroush.mediator." + (code?.ToString().ToLowerInvariant() ?? "failure") : null;
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { MediatorObservation.ReportDiagnosticFailure("signal", ex); }
    }

    private Activity? StartActivity(string name, bool enabled, TagList tags)
    {
        if (!enabled || !source.HasListeners()) return null;
        var previous = Activity.Current;
        Activity? activity = null;
        try
        {
            activity = source.StartActivity(name, ActivityKind.Internal);
            if (activity != null)
                foreach (var tag in tags) activity.SetTag(tag.Key, tag.Value);
        }
        catch (Exception ex)
        {
            Safe(() => activity?.Dispose());
            activity = null;
            MediatorObservation.ReportDiagnosticFailure("activity.start", ex);
        }
        finally { Activity.Current = previous; }
        return activity;
    }

    private static IDisposable Activate(Activity? activity)
    {
        var previous = Activity.Current;
        if (activity != null) Activity.Current = activity;
        return new Activation(previous);
    }

    private static Dictionary<string, object?> Fields(MediatorOperation op) => new()
    {
        ["OperationId"] = op.OperationId, ["ParentOperationId"] = op.Parent?.OperationId,
        ["RequestName"] = TypeName(op.RequestType), ["Kind"] = op.Kind, ["HandlerName"] = TypeName(op.HandlerType),
        ["Stage"] = op.FailureStage, ["ResultCode"] = op.ResultCode?.ToString(), ["PrimaryResultCode"] = op.PrimaryResultCode?.ToString(),
        ["TraceId"] = Activity.Current?.TraceId.ToHexString(), ["SpanId"] = Activity.Current?.SpanId.ToHexString(),
        ["ItemCount"] = op.ItemCount, ["ProducerDurationMs"] = op.ProducerDuration.TotalMilliseconds
    };

    private sealed class Operation : IMediatorOperationObservation
    {
        private readonly DiagnosticsObserver owner;
        private readonly MediatorOperation operation;
        private readonly Activity? activity;
        private readonly ILogger eventLogger;
        private readonly TagList identity;

        internal Operation(DiagnosticsObserver owner, MediatorOperation operation)
        {
            this.owner = owner; this.operation = operation;
            eventLogger = new MetadataLogger(owner.logger, operation);
            identity = new TagList
            {
                { "soroush.mediator.request.name", owner.names.Get(TypeName(operation.RequestType)) },
                { "soroush.mediator.operation.kind", operation.Kind },
                { "soroush.mediator.operation.is_root", operation.Parent is null }
            };
            activity = owner.StartActivity("mediator." + operation.Kind + " " + operation.RequestType.Name,
                owner.options.EnableTracing, identity);
            Safe(() => activity?.SetTag("soroush.mediator.request.name", TypeName(operation.RequestType)));
            if (owner.options.EnableMetrics) Safe(() => owner.active.Add(1, identity));
            using var activation = Activate();
            if (owner.options.EnableLogging) Safe(() => Log.Started(eventLogger, TypeName(operation.RequestType), null));
        }
        public IDisposable Activate() => DiagnosticsObserver.Activate(activity);
        public IMediatorStageObservation StartStage(MediatorStage stage) => new Stage(owner, operation, stage);
        public void ItemProduced()
        {
            if (owner.options.EnableMetrics && owner.options.EnableStreamItemMetrics)
                Safe(() => owner.items.Add(1, identity));
        }
        public void Complete()
        {
            using var activation = Activate();
            var error = Error(operation.Outcome, operation.ResultCode, operation.Exception);
            if (error != null) error = owner.errors.Get(error);
            var tags = identity;
            tags.Add("soroush.mediator.outcome", Outcome(operation.Outcome));
            if (error != null) tags.Add("error.type", error);
            Safe(() =>
            {
                activity?.SetTag("soroush.mediator.outcome", Outcome(operation.Outcome));
                activity?.SetTag("soroush.mediator.handler.name", TypeName(operation.HandlerType));
                activity?.SetTag("soroush.mediator.stage", operation.FailureStage);
                activity?.SetTag("soroush.mediator.result.code", operation.ResultCode?.ToString());
                activity?.SetTag("soroush.mediator.stream.item.count", operation.ItemCount);
                if (error != null) { activity?.SetTag("error.type", error); activity?.SetStatus(ActivityStatusCode.Error); }
            });
            if (owner.options.EnableMetrics)
            {
                Safe(() => owner.duration.Record(operation.Duration.TotalSeconds, tags));
                Safe(() => owner.active.Add(-1, identity));
                if (operation.Kind == "stream")
                {
                    Safe(() => owner.producerDuration.Record(operation.ProducerDuration.TotalSeconds, tags));
                    if (operation.TimeToFirstItem.HasValue)
                        Safe(() => owner.firstItem.Record(operation.TimeToFirstItem.Value.TotalSeconds, identity));
                }
            }
            if (owner.options.EnableLogging)
            {
                var level = operation.Outcome switch
                {
                    MediatorOutcome.Faulted => LogLevel.Error,
                    MediatorOutcome.Failed => owner.options.FailureLogLevel,
                    MediatorOutcome.Cancelled or MediatorOutcome.Abandoned => LogLevel.Debug,
                    _ => LogLevel.Information
                };
                Safe(() => Log.Completed(eventLogger, level, Outcome(operation.Outcome), operation.Duration.TotalMilliseconds));
                if (operation.Parent is null && operation.Exception != null &&
                    operation.Outcome == MediatorOutcome.Faulted &&
                    owner.options.ExceptionDetailsOwner == ExceptionDetailsOwner.OutermostMediator)
                {
                    var primary = operation.PrimaryException ?? (operation.Exception as HandlerStageException)?.PrimaryException;
                    if (primary != null && !ReferenceEquals(primary, operation.Exception))
                        Safe(() => Log.Exception(eventLogger, "primary", TypeName(operation.RequestType), primary));
                    Safe(() => Log.Exception(eventLogger, primary is null ? "operation" : "cleanup",
                        TypeName(operation.RequestType), operation.Exception));
                }
            }
        }
        public void Dispose() => Safe(() => activity?.Dispose());
    }

    private sealed class Stage : IMediatorStageObservation
    {
        private readonly DiagnosticsObserver owner;
        private readonly MediatorOperation operation;
        private readonly MediatorStage stage;
        private readonly Activity? activity;
        private readonly ILogger eventLogger;
        internal Stage(DiagnosticsObserver owner, MediatorOperation operation, MediatorStage stage)
        {
            this.owner = owner; this.operation = operation; this.stage = stage;
            eventLogger = new MetadataLogger(owner.logger, operation, stage);
            var tags = new TagList { { "soroush.mediator.stage", stage.Name } };
            if (stage.HandlerType != null) tags.Add(stage.Name == "Subscriber" ? "soroush.mediator.subscriber.name" :
                "soroush.mediator.handler.name", TypeName(stage.HandlerType));
            activity = owner.StartActivity("mediator.stage " + stage.Name,
                owner.options.EnableTracing && owner.options.EnableStageTracing, tags);
            using var activation = Activate();
            if (owner.options.EnableLogging) Safe(() => Log.StageStarted(eventLogger, stage.Name, null));
        }
        public IDisposable Activate() => DiagnosticsObserver.Activate(activity);
        public void Complete()
        {
            using var activation = Activate();
            var error = Error(stage.Outcome, stage.ResultCode, stage.Exception);
            if (error != null) error = owner.errors.Get(error);
            Safe(() =>
            {
                activity?.SetTag("soroush.mediator.outcome", Outcome(stage.Outcome));
                if (error != null) { activity?.SetTag("error.type", error); activity?.SetStatus(ActivityStatusCode.Error); }
            });
            if (owner.options.EnableMetrics && owner.options.EnableStageMetrics)
            {
                var tags = new TagList
                {
                    { "soroush.mediator.request.name", owner.names.Get(TypeName(operation.RequestType)) },
                    { "soroush.mediator.operation.kind", operation.Kind },
                    { "soroush.mediator.stage", owner.names.Get(stage.Name) },
                    { "soroush.mediator.outcome", Outcome(stage.Outcome) }
                };
                if (stage.Name == "Subscriber" && stage.HandlerType != null)
                    tags.Add("soroush.mediator.subscriber.name", owner.names.Get(TypeName(stage.HandlerType)));
                if (error != null) tags.Add("error.type", error);
                Safe(() => owner.stageDuration.Record(stage.Duration.TotalSeconds, tags));
            }
            if (owner.options.EnableLogging)
                Safe(() => Log.StageCompleted(eventLogger, stage.Name, Outcome(stage.Outcome), stage.Duration.TotalMilliseconds, null));
        }
        public void Dispose() => Safe(() => activity?.Dispose());
    }

    // Snapshot metadata in each generated record. Nested or deferred-export scopes must not
    // duplicate attribute keys or retain a stale handler/result snapshot from operation start.
    private sealed class MetadataLogger(ILogger target, MediatorOperation operation, MediatorStage? stage = null) : ILogger
    {
        public bool IsEnabled(LogLevel level) => target.IsEnabled(level);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => target.BeginScope(state);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = Fields(operation);
            if (stage != null)
            {
                fields["Stage"] = stage.Name;
                fields["ResultCode"] = stage.ResultCode?.ToString();
                if (stage.Name == "Subscriber") fields["SubscriberName"] = TypeName(stage.HandlerType);
            }
            if (state is IEnumerable<KeyValuePair<string, object?>> attributes)
                foreach (var pair in attributes) fields[pair.Key] = pair.Value;
            var snapshot = new MetadataState(fields.ToArray(), formatter(state, exception));
            target.Log(level, id, snapshot, exception, static (value, _) => value.Message);
        }
    }

    private sealed class MetadataState(KeyValuePair<string, object?>[] values, string message)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;
        public int Count => values.Length;
        public KeyValuePair<string, object?> this[int index] => values[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Message;
    }

    private sealed class Activation(Activity? previous) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Activity.Current = previous;
        }
    }

    private sealed class BoundedNames(int capacity)
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public string Get(string name)
        {
            lock (values)
            {
                if (values.TryGetValue(name, out var known)) return known;
                if (values.Count >= capacity) return "other";
                values.Add(name, name);
                return name;
            }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        source.Dispose();
        meter.Dispose();
    }
}

internal static class Log
{
    internal static readonly Action<ILogger, string, Exception?> Started =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1000, "soroush.mediator.operation.started"), "Mediator operation started for {RequestName}");
    internal static readonly Action<ILogger, string, Exception?> StageStarted =
        LoggerMessage.Define<string>(LogLevel.Trace, new EventId(1100, "soroush.mediator.stage.started"), "Mediator stage {Stage} started");
    internal static readonly Action<ILogger, string, string, double, Exception?> StageCompleted =
        LoggerMessage.Define<string, string, double>(LogLevel.Debug, new EventId(1101, "soroush.mediator.stage.completed"),
            "Mediator stage {Stage} completed with {Outcome} in {DurationMs} ms");
    internal static readonly Action<ILogger, string, string, Exception?> Exception =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1900, "soroush.mediator.exception"),
            "Mediator {ExceptionRole} exception for {RequestName}");
    private static readonly Action<ILogger, string, double, Exception?>[] completed = Enumerable.Range(0, 6)
        .Select(level => LoggerMessage.Define<string, double>((LogLevel)level, new EventId(1001, "soroush.mediator.operation.completed"),
            "Mediator operation completed with {Outcome} in {DurationMs} ms")).ToArray();
    internal static void Completed(ILogger logger, LogLevel level, string outcome, double milliseconds)
    {
        if (level != LogLevel.None && logger.IsEnabled(level)) completed[(int)level](logger, outcome, milliseconds, null);
    }
}
