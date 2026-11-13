using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Diagnostics;

namespace Soroush.Mediator.Diagnostics.Tests;

public partial class DiagnosticsTests
{
    [Theory]
    [InlineData("create", "stream_create")]
    [InlineData("enumerator", "enumerator_create")]
    [InlineData("move", null)]
    [InlineData("dispose", "Dispose")]
    public async Task EagerAndDeferredProducerFailuresPreserveExceptionAndEmitOnce(string phase, string? stage)
    {
        var observer = new Capture();
        var transport = new FaultingTransport(phase);
        var sender = new Mediator(transport, observer);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Collect(sender.CreateStream(new RawStream())));
        Assert.Same(transport.Failure, error);
        var op = Assert.Single(observer.Operations);
        Assert.Equal(MediatorOutcome.Faulted, op.Outcome);
        Assert.Equal(stage, op.FailureStage);
        Assert.Equal(1, observer.Completed);
    }

    [Fact]
    public async Task DisposeFailureBeforeFirstReadIsStillObserved()
    {
        var observer = new Capture();
        var transport = new FaultingTransport("dispose");
        var iterator = new Mediator(transport, observer).CreateStream(new RawStream()).GetAsyncEnumerator();
        Assert.Empty(observer.Operations);
        Assert.Same(transport.Failure, await Assert.ThrowsAsync<InvalidOperationException>(async () => await iterator.DisposeAsync()));
        Assert.Equal(MediatorOutcome.Faulted, Assert.Single(observer.Operations).Outcome);
        Assert.Equal(1, observer.Completed);
        await iterator.DisposeAsync();
        Assert.Equal(1, transport.Disposals);
    }

    [Fact]
    public async Task HandledHandlerFailureDoesNotFaultSuccessfulOperation()
    {
        var observer = new Capture();
        var services = Services(observer).AddSingleton<IPipelineBehavior<Command, HandlerResult<int>>, Recover>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(9, (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command("throw"))).Value);
        var op = Assert.Single(observer.Operations);
        Assert.Equal(MediatorOutcome.Succeeded, op.Outcome);
        Assert.Null(op.Exception);
        Assert.Null(op.FailureStage);
        Assert.Contains(observer.Stages, stage => stage.Name == "Execution" && stage.Outcome == MediatorOutcome.Faulted);
    }

    [Theory]
    [InlineData(false, MediatorOutcome.Succeeded)]
    [InlineData(true, MediatorOutcome.Rejected)]
    public async Task CustomClassificationCanHandleExpectedNegativeResultsAndFailsSafely(bool throws, MediatorOutcome outcome)
    {
        var observer = new Capture();
        var services = Services(observer).AddSingleton<IMediatorOutcomeClassifier>(new Classifier(throws));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command("Validation"));
        Assert.False(response.IsSuccess);
        Assert.Equal(outcome, Assert.Single(observer.Operations).Outcome);
    }

    [Fact]
    public async Task FirstFailedStreamItemCannotBecomeSuccessOnALaterItem()
    {
        var observer = new Capture();
        var services = Services(observer).AddSingleton<IMediatorOutcomeClassifier>(new ItemClassifier())
            .AddSingleton<IStreamRequestHandler<RawStream, int>, RawHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(new[] { 1, 2 }, await Collect(scope.ServiceProvider.GetRequiredService<ISender>().CreateStream(new RawStream())));
        Assert.Equal(MediatorOutcome.Failed, Assert.Single(observer.Operations).Outcome);
    }

    [Fact]
    public async Task RealActivitiesRemainIsolatedForInterleavedStreamsAndConcurrentRequests()
    {
        using var listener = Listen();
        using var parent = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var logs = new RecordingLogger();
        using var provider = Services().AddSingleton<ILoggerFactory>(logs).AddSoroushDiagnostics().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var stream = sender.CreateStream(new StreamQuery());
        await using (var first = stream.GetAsyncEnumerator())
        await using (var second = stream.GetAsyncEnumerator())
        {
            Assert.True(await first.MoveNextAsync());
            Assert.Same(parent, Activity.Current);
            Assert.True(await second.MoveNextAsync());
            Assert.Same(parent, Activity.Current);
            Assert.Null(MediatorObservation.Current);
        }
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
        {
            Assert.Equal(42, (await sender.Send(new Command())).Value);
            Assert.Null(MediatorObservation.Current);
            Assert.Same(parent, Activity.Current);
        })));
        var summaries = logs.Entries.Where(entry => entry.Id == 1001).ToArray();
        Assert.Equal(26, summaries.Length);
        Assert.Equal(26, summaries.Select(entry => entry.Fields["OperationId"]).Distinct().Count());
        Assert.All(summaries, entry => Assert.Null(entry.Fields["ParentOperationId"]));
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task StreamFailuresAndAbandonmentBalanceActualActiveMetrics()
    {
        var metrics = new List<Metric>();
        using var meter = Sdk.CreateMeterProviderBuilder().AddMeter(SoroushDiagnostics.MeterName).AddInMemoryExporter(metrics).Build();
        using var provider = Services().AddSoroushDiagnostics().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await Collect(sender.CreateStream(new StreamQuery { Mode = "pre" }));
        await Collect(sender.CreateStream(new StreamQuery { Mode = "item" }));
        await Assert.ThrowsAsync<HandlerStageException>(async () => await Collect(sender.CreateStream(new StreamQuery { Mode = "double-fault" })));
        await using (var iterator = sender.CreateStream(new StreamQuery()).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync());
        meter.ForceFlush();
        var active = Assert.Single(metrics, metric => metric.Name == "soroush.mediator.operation.active");
        foreach (var point in Points(active)) Assert.Equal(0, point.GetSumLong());
        var duration = Assert.Single(metrics, metric => metric.Name == "soroush.mediator.operation.duration");
        long count = 0;
        foreach (var point in Points(duration)) count += point.GetHistogramCount();
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task MetricCardinalityIsBoundedAndTraceRetainsTheRequestType()
    {
        var metrics = new List<Metric>();
        var traces = new List<Activity>();
        using var meter = Sdk.CreateMeterProviderBuilder().AddMeter(SoroushDiagnostics.MeterName).AddInMemoryExporter(metrics).Build();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddSource(SoroushDiagnostics.ActivitySourceName).AddInMemoryExporter(traces).Build();
        var services = Services().AddSoroushDiagnostics(options => { options.MaxMetricNames = 1; options.MaxMetricErrorTypes = 1; })
            .AddSingleton<IRequestHandler<VoidCommand>, VoidHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await sender.Send(new Command("Validation"));
        await sender.Send(new Command("Execution"));
        await sender.Send(new VoidCommand());
        meter.ForceFlush();
        var histogram = Assert.Single(metrics, metric => metric.Name == "soroush.mediator.operation.duration");
        var names = new HashSet<string>();
        var errors = new HashSet<string>();
        foreach (var point in Points(histogram))
        foreach (var tag in point.Tags)
        {
            if (tag.Key == "soroush.mediator.request.name") names.Add((string)tag.Value!);
            if (tag.Key == "error.type") errors.Add((string)tag.Value!);
            Assert.DoesNotContain(tag.Key, new[] { "TraceId", "UserId", "OperationId", "TenantId" });
        }
        Assert.Equal(2, names.Count);
        Assert.Contains("other", names);
        Assert.Equal(2, errors.Count);
        Assert.Contains("other", errors);
        Assert.Contains(traces, activity => Equals(activity.GetTagItem("soroush.mediator.request.name"), typeof(VoidCommand).FullName));
    }

    [Fact]
    public async Task AllDisabledSignalsUseTheSharedNoOpObserver()
    {
        var logs = new RecordingLogger();
        using var provider = Services().AddSingleton<ILoggerFactory>(logs).AddSoroushDiagnostics(options =>
        { options.EnableLogging = false; options.EnableTracing = false; options.EnableMetrics = false; }).BuildServiceProvider();
        Assert.Same(NullMediatorObserver.Instance, provider.GetRequiredService<IMediatorObserver>());
        using var scope = provider.CreateScope();
        Assert.Equal(42, (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Command())).Value);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task LocalPublisherAndDirectHandlerObservationAreAvailableExplicitly()
    {
        var observer = new Capture();
        using var provider = Services().AddSingleton<INotificationSubscriber<Notice>, Subscriber>().BuildServiceProvider();
        await new LocalNotificationPublisher(provider, observer).Publish(new Notice());
        using (var op = MediatorObservation.BeginOperation(observer, typeof(Command), "command"))
        {
            using var active = op!.Activate();
            var result = await new CommandHandler().Handle(new Command(), CancellationToken.None);
            op.RecordResult(result);
            op.Complete();
        }
        Assert.Equal(2, observer.Completed);
        Assert.Null(MediatorObservation.Current);
    }

    private sealed class Recover : IPipelineBehavior<Command, HandlerResult<int>>
    {
        public async Task<HandlerResult<int>> Handle(Command command, RequestHandlerDelegate<HandlerResult<int>> next, CancellationToken token)
        {
            try { return await next(token); }
            catch (InvalidOperationException) { return HandlerResult<int>.Success(9); }
        }
    }
    private sealed class Classifier(bool throws) : IMediatorOutcomeClassifier
    { public MediatorOutcome? Classify(object? response) => throws ? throw new Exception("classifier") : MediatorOutcome.Succeeded; }
    private sealed class ItemClassifier : IMediatorOutcomeClassifier
    { public MediatorOutcome? Classify(object? response) => response is 1 ? MediatorOutcome.Failed : MediatorOutcome.Succeeded; }
    public sealed class RawStream : IStreamableQuery<int>;
    private sealed class RawHandler : IStreamableQueryHandler<RawStream, int>
    {
        public async IAsyncEnumerable<int> Handle(RawStream query, [EnumeratorCancellation] CancellationToken token)
        { yield return 1; await Task.Yield(); yield return 2; }
    }
    private sealed class FaultingTransport(string phase) : IRequestDispatcher
    {
        public Exception Failure { get; } = new InvalidOperationException("stream producer failure");
        public int Disposals;
        public Task<T> Send<T>(IRequest<T> request, CancellationToken token = default) => throw new NotSupportedException();
        public Task Send<T>(T request, CancellationToken token = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken token = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken token = default) =>
            phase == "create" ? throw Failure : new Enumerable<T>(this, phase);
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken token = default) =>
            phase == "create" ? throw Failure : new Enumerable<object?>(this, phase);
        private sealed class Enumerable<T>(FaultingTransport transport, string phase) : IAsyncEnumerable<T>
        {
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token = default) =>
                phase == "enumerator" ? throw transport.Failure : new Enumerator<T>(transport, phase);
        }
        private sealed class Enumerator<T>(FaultingTransport transport, string phase) : IAsyncEnumerator<T>
        {
            public T Current => default!;
            public ValueTask<bool> MoveNextAsync() => phase == "move" ? throw transport.Failure : new ValueTask<bool>(false);
            public ValueTask DisposeAsync()
            {
                transport.Disposals++;
                if (phase == "dispose") throw transport.Failure;
                return default;
            }
        }
    }
}
