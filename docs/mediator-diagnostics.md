# Mediator diagnostics

The optional `Soroush.Mediator.Diagnostics` package produces structured logs,
activities and metrics. It targets `netstandard2.0` and `net8.0`. It does not install
an OpenTelemetry SDK, choose a vendor, or send data over the network.

Register the runtime and diagnostics in the application's composition root:

```csharp
using Soroush.Mediator;
using Soroush.Mediator.Diagnostics;

services.AddSoroushMediator(typeof(CreateOrderHandler).Assembly);
services.AddSoroushDiagnostics(options =>
{
    options.ExceptionDetailsOwner = ExceptionDetailsOwner.ApplicationBoundary;
});
```

Register source `Soroush.Mediator` with the application's TracerProvider, meter
`Soroush.Mediator` with its MeterProvider, and an ILogger provider for logs.
Library-generated records snapshot OperationId, stage and handler metadata into
their structured state once per record. Enable logging scopes in the OTLP provider
when the application uses scopes for its own explicit enrichment. The complete working setup is in
[the demo](../samples/Soroush.Diagnostics.Demo/Program.cs).

The SDK dependencies in the demo are pinned to 1.20.1, which has .NET 8 assets;
they are not dependencies of the diagnostics library. Package versions are managed
in Directory.Packages.props.

## Behavior and coverage

Sender operations are observed around the configured dispatcher, so typed and
object Send, void commands, streams and replacement transports all participate.
Publishing records an operation plus a stage for each local subscriber that ran.
Behaviors retain their order and can short circuit without a handler being reported.
Result handlers emit HasAccess, Validation, Execution, PreProcess and PostProcess
stages as applicable.

The default classifier recognizes HandlerResult and HandlerResult<T>. Failure
results are observed even when no exception was thrown. To recognize a different
application result type or an expected negative answer, register an
IMediatorOutcomeClassifier; returning null uses the default classification.
Classifier failures are suppressed. Responses are inspected synchronously and are
not retained by observations.

| Outcome | Meaning | Terminal log level |
| --- | --- | --- |
| succeeded | Completed successfully | Information |
| rejected | Access or validation returned failure | Information |
| failed | Execution or stream preparation returned failure | Warning, configurable |
| cancelled | Operation or effective handler token was cancelled | Debug |
| abandoned | Enumerator disposed before completion | Debug |
| faulted | Unexpected exception or cleanup failure | Error |

A handled exception followed by a successful pipeline response does not fault the
overall operation. A stage can still show its own failure. A returned business
failure sets span status Error by default; this is independent of log severity.
Successful, cancelled and abandoned operation spans leave status unset.

Disabling all three signals resolves the shared NullMediatorObserver and preserves
the normal dispatch path. Missing logger factories use NullLoggerFactory.
Observation callback, logger and listener failures are suppressed and counted;
invalid options are reported during startup/options resolution.

## Stream lifetime

Each enumeration has its own operation. Creating or disposing an unread stream
does not manufacture a successful execution. Eager adapter creation/enumerator
failures retain their original timing and are reported with phase stream_create
or enumerator_create. Producer and disposal exceptions are propagated.

Activity.Current and the mediator context are activated only
inside producer calls and restored before returning to the consumer. Consumer
pauses are included in total DurationMs but excluded from ProducerDurationMs.
This second duration is elapsed producer time, not CPU time. A consumer exception
outside producer calls is seen as early disposal, not as a producer fault.

Always dispose enumerators, normally by using await foreach. Without disposal,
cleanup and terminal delivery cannot be guaranteed. Stream access denial skips
preparation and cleanup. After access succeeds, PostProcess runs once, including
on preparation failure, early disposal and cancellation, using CancellationToken.None.
Diagnostics does not introduce a cleanup timeout or retry.

HandlerStageException keeps Code=PostProcess and its cleanup InnerException.
PrimaryException preserves an earlier producer exception and its stack. Operation
metadata also keeps PrimaryResultCode when cleanup replaces a returned failure.

## Correlation and exception ownership

OperationId and ParentOperationId work without a tracing listener. TraceId and
SpanId use the active Activity. A nested Send has its own operation; the root
metric flag means no enclosing mediator operation, not an HTTP trace root.

The default ApplicationBoundary mode emits metadata summaries and leaves complete
exception details to application middleware or the worker boundary. Such a host
should inspect HandlerStageException.PrimaryException as well as InnerException.
For a worker without a global exception logger, select OutermostMediator; the
outermost operation records each escaping cause once. Nested stages and sends do
not repeat stack traces. Do not enable another full exception logger for that same
boundary.

The library does not serialize request/response objects, DTO values, filters or
Result.Message. Full exception logging is an explicit ownership choice; exception
messages can contain sensitive application data. Host logging scopes provide
explicit enrichment: add only reviewed scalar metadata there and apply the host's
redaction policy. No payload capture switch is provided.

## Signal schema

The custom signal namespace is `soroush.mediator`. It is a Soroush schema, not an
official OpenTelemetry mediator semantic convention.

| EventId | Name | Default level |
| --- | --- | --- |
| 1000 | soroush.mediator.operation.started | Debug |
| 1001 | soroush.mediator.operation.completed | Based on outcome |
| 1100 | soroush.mediator.stage.started | Trace |
| 1101 | soroush.mediator.stage.completed | Debug |
| 1900 | soroush.mediator.exception | Error, configured owner only |

Completed summaries include RequestName, Kind, HandlerName, OperationId,
ParentOperationId, Stage, ResultCode, PrimaryResultCode, DurationMs, ItemCount,
ProducerDurationMs and available trace identifiers. Completion is attempted once
locally. Export queues/retries do not provide durable exactly-once delivery.

The separate `Soroush-Mediator-Diagnostics` EventSource reports callback failures
using EventId 1990, with a fixed reason and exception type and a five-second rate
limit. It does not recursively use ILogger.

| Instrument | Type | Unit |
| --- | --- | --- |
| soroush.mediator.operation.duration | Histogram | s |
| soroush.mediator.operation.active | UpDownCounter | {operation} |
| soroush.mediator.stage.duration | Histogram | s |
| soroush.mediator.stream.items | Counter | {item} |
| soroush.mediator.stream.time_to_first_item | Histogram | s |
| soroush.mediator.stream.producer.duration | Histogram | s |
| soroush.mediator.diagnostics.failures | ObservableCounter, process-wide callback failures | {failure} |

The operation histogram's count provides throughput. Its tags are request.name,
operation.kind, operation.is_root, outcome and failure-only error.type, with the
`soroush.mediator.` prefix on custom keys. Active counts only use the first three
identity tags so increments and decrements belong to the same series.
Stage durations add stage and, for subscriber stages, subscriber.name.

Type/stage name dimensions have a shared configurable bound (default 256), and
error types a separate bound (default 64). Each has one additional overflow value,
other. Trace and log metadata retain the actual request type. User, tenant, trace,
operation and entity identifiers are never metric dimensions. Configure histogram
buckets in SDK Views; the demo uses boundaries suitable for durations in seconds.

## Direct callers

Applications normally use the registered ISender and INotificationPublisher.
For a manually constructed sender, pass the observer explicitly:

```csharp
var sender = new Mediator(dispatcher, observer, outcomeClassifier);
var publisher = new NotificationPublisher(notificationDispatcher, observer);
```

For direct handler/dispatcher calls, use the public observation lifetime:

```csharp
using var operation = MediatorObservation.BeginOperation(observer, typeof(GetName), "query");
using var active = operation?.Activate();
try
{
    var result = await handler.Handle(request, cancellationToken);
    operation?.RecordResult(result);
}
catch (Exception exception)
{
    operation?.RecordException(exception, cancellationToken);
    throw;
}
finally
{
    operation?.Complete();
}
```

The same pattern can wrap a custom sender that bypasses the provided facade.
Do not pass scoped application services into a singleton observer.

## Verification and local platform

Run the solution tests and the SDK smoke sample:

```powershell
dotnet test Soroush.sln -c Release
dotnet run --project samples/Soroush.Diagnostics.Demo -c Release -- --in-memory
dotnet run --project samples/Soroush.Diagnostics.Demo -c Release -- --benchmark
```

The smoke benchmark compares the normal path, disabled signals, enabled signals
without listeners and a counting SDK exporter for requests and streams. Its
process allocation/elapsed measurements are useful for regression investigation;
it does not measure backend capacity or network cost.

[The local platform guide](../observability/README.md) covers Compose, Collector,
the provisioned dashboard, alerts, sampling and deployment requirements. CI
validates the Collector config and alert rules, then starts the stack and checks
that all three signals reached their respective stores.
