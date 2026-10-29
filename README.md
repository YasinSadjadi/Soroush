# Soroush

Soroush is a collection of .NET libraries for backend systems.

## Mediator abstractions

`Soroush.Mediator.Abstractions` targets .NET Standard 2.0 and .NET 8. The .NET Standard asset uses
`Microsoft.Bcl.AsyncInterfaces`, while .NET 8 uses the built-in async interfaces.
It provides request,
command and query contracts and their handlers, including streaming queries,
`ISender`, and a request pipeline behavior contract. Responses are not tied to
an application-specific result type. These are standalone Soroush contracts;
they do not implement MediatR interfaces. The separate runtime package provides
a local dispatcher; dependency injection registration is left to the application.

```csharp
using Soroush.Mediator.Abstractions;

public sealed class GetName : IQuery<string> { }

public sealed class GetNameHandler : IQueryHandler<GetName, string>
{
    public Task<string> Handle(GetName request, CancellationToken cancellationToken)
        => Task.FromResult("Soroush");
}
```

Shared build and package metadata are in `Directory.Build.props`; NuGet
dependency versions are managed centrally in `Directory.Packages.props`.
Run `dotnet test Soroush.sln -c Release` to build and test the solution, and
`dotnet pack src/Soroush.Mediator.Abstractions -c Release` to create the package.


## Mediator analyzer

Add the `Soroush.Mediator.Analyzers` NuGet package to the project containing your
commands, queries and handlers. It runs during C# compilation. For local development:

```xml
<ProjectReference Include="../Soroush.Mediator.Analyzers/Soroush.Mediator.Analyzers.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

- `SOR001` (error): a concrete request implements multiple command/query contracts.
- `SOR002` (error): a handler's response type differs from the request's declared response, including covariant conversions.
- `SOR003` (error): multiple concrete handlers for the same request occur in the compilation and referenced assemblies.
- `SOR004` (warning): no handler for a concrete source request was found in the compilation or referenced assemblies.

The analyzer checks command, query and streaming-query contracts. C# itself checks
handler method signatures and generic constraints. Analysis includes the current compilation and its referenced assemblies. Add the analyzer to
each project where diagnostics are needed. Assemblies that the current project does not
reference (including downstream projects) cannot be inspected, so SOR004 is advisory and can be
configured or disabled with `dotnet_diagnostic.SOR004.severity = none` in `.editorconfig`.
Abstract and open generic types are excluded. These diagnostics do not verify runtime
dependency-injection registration. Pack the analyzer with
`dotnet pack src/Soroush.Mediator.Analyzers -c Release`.

## Mediator runtime

`Soroush.Mediator` provides `Mediator` and `LocalRequestDispatcher`. Construct a
`Mediator` with an `IRequestDispatcher`; a transport adapter can implement this
contract to route requests to an API or message bus. The adapter is responsible
for honoring response, streaming, cancellation and error semantics. No external
transport is included. Use a provider with the appropriate scope when constructing
`LocalRequestDispatcher`.

The local dispatcher resolves `IRequestHandler<TRequest, TResponse>`,
`IRequestHandler<TRequest>` and `IStreamRequestHandler<TRequest, TResponse>`
for the concrete request type. Register these exact service types, even when the
handler implements a more specific command/query handler interface. Response
requests also resolve `IEnumerable<IPipelineBehavior<TRequest, TResponse>>`;
behaviors run in registration order around the handler. The current pipeline
contract applies to response requests only; void and stream requests invoke their
handlers directly. Object overloads require exactly one matching request contract.


## Notifications

Implement `INotification` for an event and `INotificationSubscriber<TNotification>`
for each subscriber. `new NotificationPublisher(serviceProvider)` uses the local
in-memory dispatcher by default. Use the provider for the appropriate scope. To
use another transport, pass an `INotificationDispatcher` to `NotificationPublisher`
or supply it as the second constructor argument. Inject the publisher as
`INotificationPublisher`. `LocalNotificationPublisher` is also available for local
delivery. Register each
subscriber under the exact `INotificationSubscriber<TNotification>` service type;
the provider must resolve `IEnumerable<INotificationSubscriber<TNotification>>`.

```csharp
public sealed record OrderPlaced(int OrderId) : INotification;

public sealed class AuditOrder : INotificationSubscriber<OrderPlaced>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken)
    {
        // Record the event.
        return Task.CompletedTask;
    }
}

await publisher.Publish(new OrderPlaced(42), cancellationToken);
```

The local publisher resolves subscribers for the notification's concrete runtime type,
executes them sequentially in registration order, and completes successfully when
none are registered. Cancellation is checked before each subscriber and passed to it.
An exception stops subsequent subscribers and propagates to the caller. Delivery is
in-process and awaited; it is not durable or transactional. Implement `INotificationDispatcher` for other transports or delivery policies;
the adapter defines completion, cancellation, error and delivery semantics.


## Dependency injection

Register the in-memory dispatcher and scan assemblies for closed handlers, notification
subscribers and pipeline behaviors:

```csharp
services.AddSoroushMediator(typeof(CreateOrder).Assembly);
```

Services and discovered implementations are scoped. Register open-generic behaviors
explicitly as `IPipelineBehavior<,>`. The registration is additive and can be called
multiple times. To route requests through an adapter, implement `IRequestDispatcher`
and call `services.UseRequestDispatcher<MyDispatcher>()` before or after
`AddSoroushMediator`. The adapter is responsible for the transport's semantics;
notification dispatch can be replaced before or after registration with
`services.UseNotificationDispatcher<MyNotificationDispatcher>()`.

Commands can derive from `Command` or `Command<TResponse>`, and their handlers
from `CommandHandler<TCommand>` or `CommandHandler<TCommand, TResponse>`.
These base classes are optional; the existing interfaces remain supported.


