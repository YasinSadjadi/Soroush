# Soroush Mediator Diagnostics

Optional structured logging, ActivitySource tracing and Meter metrics for Soroush.
Targets .NET Standard 2.0 and .NET 8. Install/use the Soroush.Mediator runtime too.

```csharp
using Soroush.Mediator;
using Soroush.Mediator.Diagnostics;

services.AddSoroushMediator(typeof(CreateOrderHandler).Assembly);
services.AddSoroushDiagnostics();
```

The consuming application configures its ILogger provider, OpenTelemetry SDK,
exporters and Resource. Register source and meter name `Soroush.Mediator`.
This package does not create an SDK or network connection.

Typed/object requests, void commands, streams and notifications are observed.
Result failures, cancellation, early disposal and unexpected exceptions have
distinct outcomes. Result handler stages and local notification subscribers
have optional child activities and timing metrics.

Request/response payloads and Result.Message are not captured. ApplicationBoundary
owns full exception details by default. Worker applications can select
ExceptionDetailsOwner.OutermostMediator instead of also logging the same exception
at their outer boundary.

See the [diagnostics guide](https://github.com/YasinSadjadi/Soroush/blob/main/docs/mediator-diagnostics.md)
and [working SDK sample](https://github.com/YasinSadjadi/Soroush/tree/main/samples/Soroush.Diagnostics.Demo).
