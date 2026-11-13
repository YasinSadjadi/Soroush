using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Diagnostics;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Soroush.Mediator.Diagnostics.Tests;

public partial class DiagnosticsTests
{
    [Theory]
    [InlineData(null, MediatorOutcome.Succeeded, 3)]
    [InlineData("HasAccess", MediatorOutcome.Rejected, 1)]
    [InlineData("Validation", MediatorOutcome.Rejected, 2)]
    [InlineData("Execution", MediatorOutcome.Failed, 3)]
    public async Task ResultFailuresAndSkippedStagesAreObserved(string? failure, MediatorOutcome outcome, int count)
    {
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var response = await sender.Send(new Command(failure));
        var operation = Assert.Single(observer.Operations);
        Assert.Equal(outcome, operation.Outcome);
        Assert.Equal(response.IsSuccess ? null : response.Code, operation.ResultCode);
        Assert.Equal(count, observer.Stages.Count(stage => stage.Name != "Handler"));
        Assert.Equal(typeof(CommandHandler), operation.HandlerType);
        Assert.Null(MediatorObservation.Current);
        Assert.Equal(1, observer.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoxedAndVoidRequestsAndNotificationsAreCovered(bool boxed)
    {
        var observer = new Capture();
        var services = Services(observer);
        services.AddSingleton<IRequestHandler<VoidCommand>, VoidHandler>();
        services.AddSingleton<INotificationSubscriber<Notice>, Subscriber>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        if (boxed) await sender.Send((object)new VoidCommand()); else await sender.Send(new VoidCommand());
        if (boxed) await sender.Send((object)new Command()); else await sender.Send(new Command());
        await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().Publish(new Notice());
        Assert.Equal(3, observer.Completed);
        Assert.All(observer.Operations, op => Assert.Equal(MediatorOutcome.Succeeded, op.Outcome));
        Assert.Contains(observer.Stages, stage => stage.Name == "Subscriber" && stage.HandlerType == typeof(Subscriber));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("activate")]
    [InlineData("stage.start")]
    [InlineData("stage.activate")]
    [InlineData("stage.complete")]
    [InlineData("item")]
    [InlineData("complete")]
    [InlineData("dispose")]
    public async Task ObserverFailuresNeverChangeBusinessExecution(string fail)
    {
        var observer = new Capture { ThrowAt = fail };
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.Equal(42, (await sender.Send(new Command())).Value);
        var items = await Collect(sender.CreateStream(new StreamQuery()));
        Assert.Single(items);
        Assert.Null(MediatorObservation.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamIsLazyAndRestoresCallerActivityAndContext(bool boxed)
    {
        using var listener = Listen();
        using var parent = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var logs = new RecordingLogger();
        var services = Services().AddSingleton<ILoggerFactory>(logs).AddSoroushDiagnostics();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = new StreamQuery();
        if (boxed)
        {
            var enumerable = sender.CreateStream((object)request);
            Assert.DoesNotContain(logs.Entries, entry => entry.Id == 1000);
            await using var iterator = enumerable.GetAsyncEnumerator();
            Assert.DoesNotContain(logs.Entries, entry => entry.Id == 1000);
            Assert.True(await iterator.MoveNextAsync());
            Assert.Same(parent, Activity.Current);
            Assert.Null(MediatorObservation.Current);
            Assert.False(await iterator.MoveNextAsync());
        }
        else
        {
            var enumerable = sender.CreateStream(request);
            Assert.DoesNotContain(logs.Entries, entry => entry.Id == 1000);
            await using var iterator = enumerable.GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            Assert.Same(parent, Activity.Current);
            Assert.Null(MediatorObservation.Current);
            Assert.False(await iterator.MoveNextAsync());
        }
        Assert.True(request.Cleaned);
        Assert.Single(logs.Entries, entry => entry.Id == 1001);
        Assert.Same(parent, Activity.Current);
        Assert.Null(MediatorObservation.Current);
    }

    [Theory]
    [InlineData("early", MediatorOutcome.Abandoned)]
    [InlineData("complete", MediatorOutcome.Succeeded)]
    [InlineData("access", MediatorOutcome.Rejected)]
    [InlineData("pre", MediatorOutcome.Failed)]
    [InlineData("item", MediatorOutcome.Failed)]
    public async Task StreamTerminalOutcomesAndCleanup(string mode, MediatorOutcome expected)
    {
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var query = new StreamQuery { Mode = mode };
        var stream = scope.ServiceProvider.GetRequiredService<ISender>().CreateStream(query);
        await using (var iterator = stream.GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            if (mode != "early") while (await iterator.MoveNextAsync()) { }
        }
        var op = Assert.Single(observer.Operations);
        Assert.Equal(expected, op.Outcome);
        Assert.Equal(mode != "access", query.Cleaned);
        Assert.Equal(1, observer.Completed);
        Assert.True(op.ProducerDuration <= op.Duration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherStreamTokenCanCancelAndCleanupRuns(bool cancelDispatch)
    {
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var dispatch = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var query = new StreamQuery();
        await using var iterator = scope.ServiceProvider.GetRequiredService<ISender>()
            .CreateStream(query, dispatch.Token).GetAsyncEnumerator(enumeration.Token);
        Assert.True(await iterator.MoveNextAsync());
        (cancelDispatch ? dispatch : enumeration).Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        Assert.True(query.Cleaned);
        Assert.Equal(MediatorOutcome.Cancelled, Assert.Single(observer.Operations).Outcome);
        Assert.Equal(1, observer.Completed);
    }

    [Fact]
    public async Task CleanupFailurePreservesOriginalExceptionAndStage()
    {
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var query = new StreamQuery { Mode = "double-fault" };
        var error = await Assert.ThrowsAsync<HandlerStageException>(async () =>
            await Collect(scope.ServiceProvider.GetRequiredService<ISender>().CreateStream(query)));
        Assert.Same(query.ProducerFailure, error.PrimaryException);
        Assert.Same(query.CleanupFailure, error.InnerException);
        Assert.Contains("Execute", error.PrimaryException!.StackTrace);
        Assert.Equal(HandlerResultCode.PostProcess, error.Code);
        var op = Assert.Single(observer.Operations);
        Assert.Equal(MediatorOutcome.Faulted, op.Outcome);
        Assert.Equal("PostProcess", op.FailureStage);
        Assert.Equal(1, observer.Completed);
    }

    [Fact]
    public async Task InterleavedEnumerationsHaveIndependentOperationsAndDisposeOnce()
    {
        using var listener = Listen();
        using var caller = new Activity("caller").Start();
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<ISender>().CreateStream(new StreamQuery());
        var first = stream.GetAsyncEnumerator();
        var second = stream.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(2, observer.Operations.Count);
        Assert.NotEqual(observer.Operations[0].OperationId, observer.Operations[1].OperationId);
        Assert.All(observer.Operations, op => Assert.Null(op.Parent));
        Assert.Null(MediatorObservation.Current);
        Assert.Same(caller, Activity.Current);
        await first.DisposeAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(2, observer.Completed);
    }

    [Fact]
    public async Task EmptyAndNeverReadStreamsHaveCorrectLifetimes()
    {
        var observer = new Capture();
        using var provider = Services(observer).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await sender.CreateStream(new StreamQuery()).GetAsyncEnumerator().DisposeAsync();
        Assert.Empty(observer.Operations);
        Assert.Empty(await Collect(sender.CreateStream(new StreamQuery { Mode = "empty" })));
        var op = Assert.Single(observer.Operations);
        Assert.Equal(0, op.ItemCount);
        Assert.Null(op.TimeToFirstItem);
        Assert.Equal(MediatorOutcome.Succeeded, op.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementPipelineTokenAndUnrelatedCancellationAreDistinguished(bool unrelated)
    {
        var observer = new Capture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var services = Services(observer);
        if (!unrelated) services.AddSingleton<IPipelineBehavior<Command, HandlerResult<int>>>(new ReplaceToken(cancelled.Token));
        else services.AddSingleton<IRequestHandler<Command, HandlerResult<int>>>(new CancelledHandler(cancelled.Token));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command()));
        Assert.Equal(unrelated ? MediatorOutcome.Faulted : MediatorOutcome.Cancelled, Assert.Single(observer.Operations).Outcome);
    }

    [Fact]
    public async Task StructuredLogsContainMetadataButNoPayloadOrFailureMessage()
    {
        var logs = new RecordingLogger();
        using var provider = Services().AddSingleton<ILoggerFactory>(logs).AddSoroushDiagnostics().BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command("Validation"));
        var completed = Assert.Single(logs.Entries, entry => entry.Id == 1001);
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("rejected", completed.Fields["Outcome"]);
        Assert.Equal("Validation", completed.Fields["ResultCode"]);
        Assert.Equal("Validation", completed.Fields["Stage"]);
        Assert.NotNull(completed.Fields["OperationId"]);
        Assert.Null(completed.Exception);
        Assert.DoesNotContain("SECRET", string.Join(" ", logs.Entries.SelectMany(entry => entry.Fields.Values)));
    }

    [Theory]
    [InlineData(ExceptionDetailsOwner.ApplicationBoundary, 0)]
    [InlineData(ExceptionDetailsOwner.OutermostMediator, 1)]
    public async Task NestedExceptionsHaveOneConfiguredDetailsOwner(ExceptionDetailsOwner owner, int details)
    {
        var logs = new RecordingLogger();
        var services = Services().AddSingleton<ILoggerFactory>(logs)
            .AddSoroushDiagnostics(options => options.ExceptionDetailsOwner = owner);
        services.AddScoped<IRequestHandler<Outer, int>, OuterHandler>();
        services.AddSingleton<IRequestHandler<Boom, int>, BoomHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Outer()));
        Assert.Equal(2, logs.Entries.Count(entry => entry.Id == 1001));
        Assert.Equal(details, logs.Entries.Count(entry => entry.Id == 1900));
    }

    [Fact]
    public async Task ActualSdkExportsCorrelatedTracesLogsAndMetrics()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        var logs = new List<LogRecord>();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddSource(SoroushDiagnostics.ActivitySourceName)
            .AddInMemoryExporter(activities).Build();
        using var meter = Sdk.CreateMeterProviderBuilder().AddMeter(SoroushDiagnostics.MeterName)
            .AddInMemoryExporter(metrics).Build();
        using var logger = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace)
            .AddOpenTelemetry(options => { options.IncludeScopes = true; options.AddInMemoryExporter(logs); }));
        using var provider = Services().AddSingleton<ILoggerFactory>(logger).AddSoroushDiagnostics().BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command("Validation"));
        meter.ForceFlush();
        tracer.ForceFlush();
        var operation = Assert.Single(activities, activity => activity.DisplayName.StartsWith("mediator.command "));
        Assert.Equal(ActivityStatusCode.Error, operation.Status);
        Assert.Equal("soroush.mediator.validation", operation.GetTagItem("error.type"));
        Assert.DoesNotContain(activities, activity => activity.DisplayName == "mediator.stage Execution");
        var summary = Assert.Single(logs, log => log.EventId.Id == 1001);
        Assert.NotNull(summary.Attributes);
        Assert.All(summary.Attributes!.GroupBy(pair => pair.Key), group => Assert.Single(group));
        Assert.Equal(typeof(CommandHandler).FullName, Assert.Single(summary.Attributes!, pair => pair.Key == "HandlerName").Value);
        Assert.Equal("Validation", Assert.Single(summary.Attributes!, pair => pair.Key == "Stage").Value);
        Assert.All(logs, log =>
        {
            if (log.Attributes != null) Assert.All(log.Attributes.GroupBy(pair => pair.Key), group => Assert.Single(group));
        });
        Assert.Equal(operation.TraceId, summary.TraceId);
        Assert.Equal(operation.SpanId, summary.SpanId);
        var histogram = Assert.Single(metrics, metric => metric.Name == "soroush.mediator.operation.duration");
        Assert.Equal("s", histogram.Unit);
        foreach (var point in Points(histogram)) Assert.Equal(1, point.GetHistogramCount());
        var active = Assert.Single(metrics, metric => metric.Name == "soroush.mediator.operation.active");
        foreach (var point in Points(active))
        {
            Assert.Equal(0, point.GetSumLong());
            foreach (var tag in point.Tags) Assert.DoesNotContain(tag.Key, new[] { "soroush.mediator.outcome", "error.type" });
        }
    }

    [Fact]
    public async Task ThrowingLoggerAndMetricListenerCannotChangeTheResult()
    {
        var logger = new RecordingLogger { Throw = true };
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            { if (instrument.Meter.Name == SoroushDiagnostics.MeterName) meterListener.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new Exception("metric sink"));
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new Exception("metric sink"));
        listener.Start();
        using var provider = Services().AddSingleton<ILoggerFactory>(logger).AddSoroushDiagnostics().BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(42, (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command())).Value);
        Assert.Null(MediatorObservation.Current);
        Assert.True(MediatorObservation.FailureCount > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationOrderAndRepeatingDiagnosticsPreserveDispatcherAndScopes(bool before)
    {
        var services = new ServiceCollection();
        if (before) services.AddSoroushDiagnostics();
        services.UseRequestDispatcher<Transport>().AddSoroushMediator();
        services.AddSoroushDiagnostics().AddSoroushDiagnostics();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.IsType<Transport>(scope.ServiceProvider.GetRequiredService<IRequestDispatcher>());
        Assert.Equal(7, (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command())).Value);
        Assert.Single(services, service => service.ServiceType == typeof(IMediatorObserver));
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ISender>());
    }

    [Fact]
    public void InvalidOptionsAreRejectedAndNoLoggerIsRequired()
    {
        using var provider = Services().AddSoroushDiagnostics(options => options.MaxMetricNames = 0).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IMediatorObserver>());
    }

    private static IServiceCollection Services(IMediatorObserver? observer = null)
    {
        var services = new ServiceCollection().AddSoroushMediator();
        services.AddSingleton<IRequestHandler<Command, HandlerResult<int>>, CommandHandler>();
        services.AddSingleton<IStreamRequestHandler<StreamQuery, HandlerResult<int>>, StreamHandler>();
        if (observer != null) services.AddSingleton(observer);
        return services;
    }
    private static MetricPoint[] Points(Metric metric)
    {
        var points = new List<MetricPoint>();
        foreach (ref readonly var point in metric.GetMetricPoints()) points.Add(point);
        return points.ToArray();
    }
    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SoroushDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    public sealed record Command(string? Failure = null) : ICommand<HandlerResult<int>>;
    public sealed class CommandHandler : ResultCommandHandler<Command, int>
    {
        protected override Task<HandlerResult> HasAccess(Command request, CancellationToken token) =>
            Task.FromResult(request.Failure == "HasAccess" ? HandlerResult.Failure(HandlerResultCode.Execution, "SECRET") : HandlerResult.Success());
        protected override Task<HandlerResult> Validate(Command request, CancellationToken token) =>
            Task.FromResult(request.Failure == "Validation" ? HandlerResult.Failure(HandlerResultCode.Execution, "SECRET") : HandlerResult.Success());
        protected override Task<HandlerResult<int>> Execute(Command request, CancellationToken token) =>
            request.Failure == "throw" ? throw new InvalidOperationException("producer SECRET") :
            Task.FromResult(request.Failure == "Execution" ? HandlerResult<int>.Failure(HandlerResultCode.Execution, "SECRET") : HandlerResult<int>.Success(42));
    }
    public sealed class StreamQuery : IStreamableQuery<HandlerResult<int>>
    {
        public string Mode { get; init; } = "complete";
        public bool Cleaned { get; set; }
        public Exception ProducerFailure { get; } = new InvalidOperationException("producer SECRET");
        public Exception CleanupFailure { get; } = new InvalidOperationException("cleanup SECRET");
    }
    public sealed class StreamHandler : ResultStreamableQueryHandler<StreamQuery, int>
    {
        protected override Task<HandlerResult> HasAccess(StreamQuery query, CancellationToken token) =>
            Task.FromResult(query.Mode == "access" ? HandlerResult.Failure(HandlerResultCode.HasAccess) : HandlerResult.Success());
        protected override Task<HandlerResult> PreProcess(StreamQuery query, CancellationToken token) =>
            Task.FromResult(query.Mode == "pre" ? HandlerResult.Failure(HandlerResultCode.Execution) : HandlerResult.Success());
        protected override async IAsyncEnumerable<HandlerResult<int>> Execute(StreamQuery query, [EnumeratorCancellation] CancellationToken token)
        {
            if (query.Mode == "empty") yield break;
            yield return HandlerResult<int>.Success(1);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            if (query.Mode == "double-fault") throw query.ProducerFailure;
            if (query.Mode == "item") yield return HandlerResult<int>.Failure(HandlerResultCode.Execution);
        }
        protected override Task<HandlerResult> PostProcess(StreamQuery query, CancellationToken token)
        {
            Assert.False(token.IsCancellationRequested);
            query.Cleaned = true;
            if (query.Mode == "double-fault") throw query.CleanupFailure;
            return Task.FromResult(HandlerResult.Success());
        }
    }
    public sealed class VoidCommand : ICommand;
    public sealed class VoidHandler : ICommandHandler<VoidCommand>
    { public Task Handle(VoidCommand command, CancellationToken token) => Task.CompletedTask; }
    public sealed class Notice : INotification;
    public sealed class Subscriber : INotificationSubscriber<Notice>
    { public Task Handle(Notice notice, CancellationToken token) => Task.CompletedTask; }
    private sealed class ReplaceToken(CancellationToken replacement) : IPipelineBehavior<Command, HandlerResult<int>>
    {
        public Task<HandlerResult<int>> Handle(Command request, RequestHandlerDelegate<HandlerResult<int>> next, CancellationToken token) => next(replacement);
    }
    private sealed class CancelledHandler(CancellationToken unrelated) : IRequestHandler<Command, HandlerResult<int>>
    { public Task<HandlerResult<int>> Handle(Command command, CancellationToken token) => Task.FromCanceled<HandlerResult<int>>(unrelated); }
    public sealed class Outer : IQuery<int>;
    public sealed class Boom : IQuery<int>;
    public sealed class OuterHandler(ISender sender) : IQueryHandler<Outer, int>
    { public Task<int> Handle(Outer query, CancellationToken token) => sender.Send(new Boom(), token); }
    public sealed class BoomHandler : IQueryHandler<Boom, int>
    { public Task<int> Handle(Boom query, CancellationToken token) => throw new InvalidOperationException("boom"); }
    public sealed class Transport : IRequestDispatcher
    {
        public Task<T> Send<T>(IRequest<T> request, CancellationToken token = default) => Task.FromResult((T)(object)HandlerResult<int>.Success(7));
        public Task Send<T>(T request, CancellationToken token = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken token = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken token = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Capture : IMediatorObserver
    {
        public string? ThrowAt { get; init; }
        public List<MediatorOperation> Operations { get; } = new();
        public List<MediatorStage> Stages { get; } = new();
        public int Completed { get; private set; }
        private void Check(string phase) { if (ThrowAt == phase) throw new Exception("observer failure"); }
        public IMediatorOperationObservation StartOperation(MediatorOperation operation)
        { Check("start"); Operations.Add(operation); return new Observation(this); }
        private sealed class Observation(Capture capture) : IMediatorOperationObservation
        {
            public IDisposable? Activate() { capture.Check("activate"); return null; }
            public IMediatorStageObservation StartStage(MediatorStage stage)
            { capture.Check("stage.start"); capture.Stages.Add(stage); return new StageObservation(capture); }
            public void ItemProduced() => capture.Check("item");
            public void Complete() { capture.Check("complete"); capture.Completed++; }
            public void Dispose() => capture.Check("dispose");
        }
        private sealed class StageObservation(Capture capture) : IMediatorStageObservation
        {
            public IDisposable? Activate() { capture.Check("stage.activate"); return null; }
            public void Complete() => capture.Check("stage.complete");
            public void Dispose() => capture.Check("dispose");
        }
    }
    private sealed record Entry(int Id, LogLevel Level, Dictionary<string, object?> Fields, Exception? Exception);
    private sealed class RecordingLogger : ILoggerFactory, ILogger
    {
        private readonly LoggerExternalScopeProvider scopes = new();
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public bool Throw { get; init; }
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => !Throw ? true : throw new Exception("logger");
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            !Throw ? scopes.Push(state) : throw new Exception("scope");
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new Exception("log sink");
            var fields = new Dictionary<string, object?>();
            scopes.ForEachScope((scope, values) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> attributes)
                    foreach (var attribute in attributes) values[attribute.Key] = attribute.Value;
            }, fields);
            if (state is IEnumerable<KeyValuePair<string, object?>> attributes)
                foreach (var attribute in attributes) fields[attribute.Key] = attribute.Value;
            Entries.Enqueue(new Entry(id.Id, level, fields, exception));
        }
        public void Dispose() { }
    }
}
