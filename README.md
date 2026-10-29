# Soroush

Soroush is a collection of .NET libraries for backend systems.

## Projected queries

`Soroush.Mediator.Abstractions` runs projected mediator queries with filters and ordering addressed
by the **output DTO's field names**. The base query carries request options. A concrete
handler derives from the base handler, specifies its entity and output types, and
defines the `Select` mapping and permitted filterable/orderable DTO members. Client
filtering and ordering are denied unless registered in that handler. After validating
the request against those permissions, the base handler's `Handle` method applies:

`Select(mapping)` → output filters → output ordering → paging → provider execution.

```csharp
using System.Linq.Expressions;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

public sealed class OrderDto
{
    public Guid Id { get; init; }
    public decimal Amount { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class GetOrders : ProjectedQuery<OrderDto> { }

public sealed class GetOrdersHandler(IQueryContext context)
    : ProjectedQueryHandler<GetOrders, Order, OrderDto>(context)
{
    protected override Expression<Func<Order, OrderDto>> Mapping => order => new OrderDto
    {
        Id = order.Id,
        Amount = order.Total,
        CustomerName = order.CustomerName,
        CreatedAt = order.CreatedAt,
        Description = order.Description
    };

    protected override void ConfigureFilterableFields(QueryFieldRegistry<OrderDto> fields)
    {
        fields.Add(dto => dto.Amount)
              .Add(dto => dto.CustomerName);
    }

    protected override void ConfigureOrderableFields(QueryFieldRegistry<OrderDto> fields)
    {
        fields.Add(dto => dto.Id)
              .Add(dto => dto.Amount)
              .Add(dto => dto.CreatedAt);
    }
}
```

`GetOrders` inherits the filter, sort, and paging inputs. `GetOrdersHandler` owns the
entity and mapping and inherits the complete execution flow, so it needs no custom
`Handle` implementation. Its two configuration overrides register independent
permissions using typed DTO members; names are inferred automatically. Register the application's query adapter and
scan the assembly containing concrete handlers once:

```csharp
services.AddSoroushQueries<AppQueryContext>(typeof(GetOrdersHandler).Assembly);
```

This registers the mediator and scoped concrete handlers using normal mediator discovery,
including handlers that inherit their request contract through the base class. Abstract
and open generic handlers are skipped. Each query requires its own concrete handler.
Repeated registration is deduplicated, and existing `IQueryContext` registrations are
preserved. Normal mediator pipeline behaviors run before the base handler's execution.

The example above permits:

| Output field | Client filtering | Client ordering |
| --- | --- | --- |
| `Id` | Denied | Allowed |
| `Amount` | Allowed | Allowed |
| `CustomerName` | Allowed | Denied |
| `CreatedAt` | Denied | Allowed |
| `Description` | Denied | Denied |

Adding a public DTO member does not grant either permission. With no configuration,
both client operations are denied for every field. A field can be returned for display
without allowing clients to filter or sort by it. Response visibility is controlled
separately by the mapping: omit a member from the DTO and mapping if it must not be returned.
Permissions belong to the handler, so two handlers can use the same DTO with different policies.

Send a request using the output name `Amount`, even though the entity field is `Total`:

```csharp
using Soroush.Mediator.Abstractions;

var result = await sender.Send(new GetOrders
{
    FilterBy = new[] { new QueryFilter("Amount", FilterOperator.GreaterThanOrEqual, "100") },
    OrderBy = new[] { new QuerySort("Amount", descending: true) },
    PageNumber = 1,
    PageSize = 20
}, cancellationToken);
```

The response is `PagedResult<OrderDto>`. The handler counts selected and filtered rows
before ordering and paging, then loads only the requested page. Both operations run
sequentially on the same scoped query adapter and receive the cancellation token.

Several registered filters combine with AND, and sort keys apply in their supplied order:

```csharp
var result = await sender.Send(new GetOrders
{
    FilterBy = new[]
    {
        new QueryFilter("Amount", FilterOperator.GreaterThanOrEqual, "100"),
        new QueryFilter("CustomerName", FilterOperator.Contains, "Sara")
    },
    OrderBy = new[]
    {
        new QuerySort("CreatedAt", descending: true),
        new QuerySort("Amount")
    },
    PageNumber = 2,
    PageSize = 10
}, cancellationToken);
```

Filtering `CreatedAt` or ordering by `CustomerName` in this example throws an
`ArgumentException`, even though both properties exist on the DTO. A request containing
any unregistered field is rejected in full, including when it combines filters using OR.
The base handler validates permissions before calling the query context.

`FilterBy`, `OrderBy`, `Logic`, `PageNumber`, and `PageSize` are inherited model-bindable
properties. For example, with the application's `JsonStringEnumConverter` configured:

```json
{
  "filterBy": [{ "field": "Amount", "operator": "GreaterThanOrEqual", "value": "100" }],
  "orderBy": [{ "field": "Amount", "descending": true }],
  "pageNumber": 1,
  "pageSize": 20
}
```

Registered names resolve case-insensitively to public readable properties or public
instance fields directly on the selected DTO; indexers, static members, unregistered
members, and entity-only fields are excluded. Register computed or navigation-derived
values through a flat DTO member in the mapping. The registry requires direct DTO selectors.
Renamed, computed, constructor-projected, and navigation-derived DTO fields work through
the mapping. Map requested output members in the `Select` expression so the provider
can translate them. `Mapping` is a protected member of the handler; query requests
contain only input properties.

`fields.Add("total", dto => dto.Amount)` registers an explicit alias instead of the CLR
name. Only that alias is permitted for the configured operation; it does not also
permit `Amount`. Duplicate names in one registry are rejected. The same member may be
registered independently in both filterable and orderable registries.

Filters default to AND. Set `Logic = FilterLogic.Or` for OR within the request filters;
source restrictions remain in effect. Scalar `Value` uses invariant-culture text.
`In` and `NotIn` use `Values = new[] { "100", "200" }`; null checks require no value.
The operators listed below also apply to these requests. Invalid fields, values,
operators, or page sizes fail before the base handler accesses the data source.

Pages default to number 1 and size 20. A projected `Id` is used as the default order
and appended as a tie breaker when the request sorts by other fields. Keep it unique
within the result. This fixed server ordering does not grant clients permission to
request filtering or ordering by `Id`; explicitly register it for those operations.
If the DTO has no `Id`, register and supply a named ordering containing a unique output
key. Apply application limits to page sizes, filter counts, and set sizes.

### Query provider adapter

Implement `IQueryContext` once for the application's provider. For example, in an
application that already uses EF Core:

```csharp
using Microsoft.EntityFrameworkCore;
using Soroush.Mediator.Abstractions;

public sealed class AppQueryContext(AppDbContext db) : IQueryContext
{
    public IQueryable<TEntity> Query<TEntity>() where TEntity : class
        => db.Set<TEntity>().AsNoTracking();

    public Task<long> CountAsync<TResult>(IQueryable<TResult> query,
        CancellationToken cancellationToken = default)
        => query.LongCountAsync(cancellationToken);

    public async Task<IReadOnlyList<TResult>> ListAsync<TResult>(IQueryable<TResult> query,
        CancellationToken cancellationToken = default)
        => await query.ToListAsync(cancellationToken);
}
```

Register the database context separately. Apply mandatory tenant, authorization, and
soft deletion restrictions in the query source or the provider's global filters.
The package builds deferred query expressions and has no EF Core dependency. The
adapter supplies native asynchronous execution; there is no implicit synchronous or
client evaluation fallback. Concurrent writes can make the count and items differ
unless the application uses an appropriate read snapshot.

The package targets .NET Standard 2.0 and .NET 8 and includes projected query execution. Register the query adapter with `AddSoroushQueries`
from `Soroush.Mediator`. Query contracts and base handlers live in `Soroush.Mediator.Abstractions`.

### Existing Select queries

For a query that already has a `Select`, use `QueryOptions` with explicit permission maps:

```csharp
using Soroush.Mediator.Abstractions;

var options = new QueryOptions
{
    FilterBy = new[] { new QueryFilter("Amount", FilterOperator.GreaterThan, "100") },
    OrderBy = new[] { new QuerySort("Amount", descending: true) }
};

var filterable = new QueryFieldMap<OrderDto>()
    .Map("Amount", dto => dto.Amount);
var orderable = new QueryFieldMap<OrderDto>()
    .Map("Amount", dto => dto.Amount);

var pageQuery = db.Orders
    .Select(order => new OrderDto { Id = order.Id, Amount = order.Total })
    .ApplyQuery(options, filterable, orderable);
```

The lower-level `ApplyQuery(options)` and `ToQuery<TDto>()` convenience overloads
allow all public DTO members and do not enforce a handler's policy. For external
requests, execute the configured handler or use the overloads accepting explicit maps.

`handler.ApplyTo(source, request)` builds the same selected, filtered, ordered, and
paged query over a supplied entity source without the mediator or materialization.
It preserves existing source predicates. For request-specific entity restrictions
during normal handler execution, override `CreateQuery(request)` and extend
`base.CreateQuery(request)`; these restrictions apply before `Select` and the output
filters. `QueryOptions.ToQuery(filterable, orderable)` is available when an adapter
needs to inspect the validated specification and page with explicit permissions.

## Domain abstractions

`Soroush.Domain.Abstractions` targets .NET Standard 2.0 and .NET 8. The .NET Standard
asset references `System.ComponentModel.Annotations` for mapping annotations.
It provides `Entity`, `IEntity<TKey>`, `IDomainEvent`, `IDomainEventSource`, `IAuditable`, and `ISoftDeletable`.

### Base entity

`Entity` mirrors the base entity in Sheng.Ai: public read/write `Guid Id`,
`DateTime CreatedAt`, and nullable `DateTime? ModifiedAt`. It implements
`IEntity<Guid>` so derived entities share the identity contract.
Values retain their CLR defaults until assigned; identity generation and timestamp
updates belong to the application or persistence adapter.

```csharp
public sealed class Customer : Entity
{
    public string Name { get; set; } = string.Empty;
}
```

Call `Touch()` to set `ModifiedAt` to the current UTC time. Call
`SuppressModifiedAtUpdate()` to preserve its current value across subsequent touches,
and `SuppressModifiedAtUpdate(false)` to enable updates again. Both methods are virtual.
Database adapters should call `Touch()` instead of assigning `ModifiedAt` directly to
honor suppression; the public setter remains available for explicit assignments.

The instance flag `IsModifiedAtUpdateSuppressed` uses the standard .NET
`System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute`, allowing adapters
such as EF Core to recognize it. Apply `[NotMapped]` to
other members that should be excluded from mapping; applying it to a class excludes
the entire entity type. Adapters that do not support annotations must explicitly
honor this metadata.

`Entity` does not implement `IAuditable`, whose separate contract uses
`DateTimeOffset CreatedAt` and `DateTimeOffset? UpdatedAt`.

## Mediator abstractions

`Soroush.Mediator.Abstractions` targets .NET Standard 2.0 and .NET 8. The .NET Standard asset uses
`Microsoft.Bcl.AsyncInterfaces`, while .NET 8 uses the built-in async interfaces.
It provides request,
command and query contracts and their handlers, including streaming queries,
`ISender`, and a request pipeline behavior contract. Responses are not tied to
an application-specific result type. Projected queries, their base handlers, and filtering, ordering, and paging helpers also live in this package. These are Soroush contracts;
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

To collect line and branch coverage for the libraries and analyzer:

```sh
dotnet test Soroush.sln -c Release --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory .artifacts/coverage
```

Each test project writes its own `coverage.cobertura.xml` below the results directory.
GitHub Actions uploads these reports in the `code-coverage` artifact. Coverage is
measured on the .NET 8 assets; the solution build also compiles the .NET Standard assets.


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

The analyzer checks command, query and streaming-query contracts, including handlers that implement the base request handler interfaces. C# itself checks
handler method signatures and generic constraints. Analysis includes the current compilation and its referenced assemblies. Add the analyzer to
each project where diagnostics are needed. Assemblies that the current project does not
reference (including downstream projects) cannot be inspected, so SOR004 is advisory and can be
configured or disabled with `dotnet_diagnostic.SOR004.severity = none` in `.editorconfig`.
Abstract and open generic types are excluded. Concrete handlers derived from
`ProjectedQueryHandler<TQuery, TEntity, TResult>` satisfy their query contracts;
a projected query without a concrete handler produces SOR004 as usual.
These diagnostics do not verify runtime
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



## CI and NuGet releases

GitHub Actions restores and tests the solution and uploads all six NuGet packages
on pushes to `main`, pull requests, and manual runs. These builds use the version in
`Directory.Build.props` and do not publish to NuGet.org.

To publish, create a NuGet.org API key with permission to push new packages and
versions for `Soroush.*`, and add it as the repository Actions secret
`NUGET_API_KEY` (Settings > Secrets and variables > Actions). Do not commit the key.
Ensure the package IDs are available or owned by your NuGet account. Push a version
tag such as `v0.1.2` to run the tests, build all packages with that version,
and publish them to NuGet.org:

```sh
git tag v0.1.2
git push origin v0.1.2
```

Use a new tag and version for each release. Existing package versions are skipped
on a retry; NuGet does not allow replacing an already published version. A failed
release can be retried from the Actions page after fixing the problem. Prerelease
versions such as `v0.2.0-beta.1` are supported. Package publication may take time to
appear on NuGet.org.
