using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;
using Soroush.Mediator.Diagnostics;

if (args.Contains("--benchmark"))
{
    await Benchmarks.Run();
    return;
}
var inMemory = args.Contains("--in-memory");
var activities = new List<Activity>();
var metrics = new List<Metric>();
var records = new List<LogRecord>();
var resource = ResourceBuilder.CreateDefault().AddService("soroush-diagnostics-demo", serviceVersion: "1.0.0",
    serviceInstanceId: Guid.NewGuid().ToString("N")).AddAttributes(new Dictionary<string, object>
    { ["deployment.environment.name"] = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "development" });
var ratio = double.Parse(Environment.GetEnvironmentVariable("OTEL_TRACES_SAMPLER_ARG") ?? "1", CultureInfo.InvariantCulture);
if (ratio < 0 || ratio > 1) throw new ArgumentOutOfRangeException(nameof(ratio), "Sampling ratio must be in 0..1.");
var traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(SoroushDiagnostics.ActivitySourceName)
    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(inMemory ? 1 : ratio)));
var meters = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(SoroushDiagnostics.MeterName)
    .AddView("soroush.mediator.*", new ExplicitBucketHistogramConfiguration
    { Boundaries = new double[] { .001, .005, .01, .025, .05, .1, .25, .5, 1, 2.5, 5, 10, 30 } });
if (inMemory) { traces.AddInMemoryExporter(activities); meters.AddInMemoryExporter(metrics); }
else { traces.AddOtlpExporter(); meters.AddOtlpExporter(); }
using var traceProvider = traces.Build();
using var meterProvider = meters.Build();
using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(resource);
    options.IncludeScopes = true;
    if (inMemory) options.AddInMemoryExporter(records); else options.AddOtlpExporter();
}));
var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(loggerFactory);
services.AddSoroushMediator(typeof(CreateOrderHandler).Assembly);
services.AddSoroushDiagnostics(options => options.ExceptionDetailsOwner = ExceptionDetailsOwner.OutermostMediator);
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
using var scope = provider.CreateScope();
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
await sender.Send(new CreateOrder(10));
await sender.Send(new CreateOrder(-1));
await sender.Send(new TouchOrder());
await scope.ServiceProvider.GetRequiredService<INotificationPublisher>().Publish(new OrderCreated());
await foreach (var _ in sender.CreateStream(new OrderStream())) { }
await foreach (var _ in sender.CreateStream(new OrderStream())) break;
traceProvider.ForceFlush(5000);
meterProvider.ForceFlush(5000);
// Disposing the logger factory flushes the batch log exporter with its bounded shutdown timeout.
loggerFactory.Dispose();
if (inMemory && (activities.Count == 0 || metrics.Count == 0 || records.All(log => log.EventId.Id != 1001)))
    throw new InvalidOperationException("Expected all three telemetry signals.");
Console.WriteLine(inMemory
    ? $"Exported {activities.Count} activities, {metrics.Count} metrics and {records.Count} logs to memory."
    : "Sent six mediator operations over OTLP. Open the Soroush Mediator dashboard at http://localhost:3000.");

public sealed record CreateOrder(decimal Amount) : ICommand<HandlerResult<int>>;
public sealed class CreateOrderHandler : ResultCommandHandler<CreateOrder, int>
{
    protected override Task<HandlerResult> Validate(CreateOrder command, CancellationToken token) =>
        Task.FromResult(command.Amount > 0 ? HandlerResult.Success() : HandlerResult.Failure(HandlerResultCode.Validation));
    protected override Task<HandlerResult<int>> Execute(CreateOrder command, CancellationToken token) =>
        Task.FromResult(HandlerResult<int>.Success(42));
}
public sealed class TouchOrder : ICommand;
public sealed class TouchOrderHandler : ICommandHandler<TouchOrder>
{ public Task Handle(TouchOrder command, CancellationToken token) => Task.CompletedTask; }
public sealed class OrderCreated : INotification;
public sealed class OrderSubscriber : INotificationSubscriber<OrderCreated>
{ public Task Handle(OrderCreated notification, CancellationToken token) => Task.CompletedTask; }
public sealed class OrderStream : IStreamableQuery<HandlerResult<int>>;
public sealed class OrderStreamHandler : ResultStreamableQueryHandler<OrderStream, int>
{
    protected override async IAsyncEnumerable<HandlerResult<int>> Execute(OrderStream query, [EnumeratorCancellation] CancellationToken token)
    {
        for (var i = 0; i < 3; i++) { token.ThrowIfCancellationRequested(); yield return HandlerResult<int>.Success(i); await Task.Yield(); }
    }
}
