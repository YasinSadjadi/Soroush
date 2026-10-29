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
using Soroush.Persistence.Abstractions;

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

The package targets .NET Standard 2.0 and .NET 8 and depends on `Soroush.Persistence`
for projected query execution. Register the query adapter with `AddSoroushQueries`
from `Soroush.Mediator`. Query contracts and base handlers live in `Soroush.Mediator.Abstractions`.

### Existing Select queries

For a query that already has a `Select`, use `QueryOptions` with explicit permission maps:

```csharp
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

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

## Domain and persistence abstractions

These independently packable libraries target .NET Standard 2.0 and .NET 8, like
the mediator contracts. No EF Core, Dapper, Redis, or mediator runtime is required.

| Package | Contracts and helpers | Dependencies |
| --- | --- | --- |
| `Soroush.Domain.Abstractions` | `IEntity<TKey>`, `IDomainEvent`, `IDomainEventSource`, `IAuditable`, `ISoftDeletable` | None |
| `Soroush.Persistence.Abstractions` | Repositories, `IUnitOfWork`, `ITransaction`, `IPersistenceSession`, lifecycle interceptor contracts, connection factory, specifications, advanced queries, field maps, sorting, paging | Domain abstractions; async interface compatibility package on .NET Standard only |
| `Soroush.Persistence` | `UnitOfWork`, transaction execution, interceptor base classes, repository scanning, scoped DI registration, query application helpers | Persistence abstractions; DI abstractions |

The abstraction packages define contracts. `Soroush.Persistence` implements the unit
of work coordinator and interceptor lifecycle. Supply an `IPersistenceSession` adapter
for your database or ORM; repositories must use the same underlying session. Auditing,
soft deletion and domain event dispatch remain application or adapter policies.

### Repository queries

Repositories constrain entities to `class, IEntity<TKey>` and accept specifications
instead of exposing `IQueryable` or provider-specific types. Predicates compose using
`And`, `Or`, and `Not` without invocation expressions. Composition preserves the
left-hand specification's sort keys; the right-hand ordering is ignored. Adapters
translate predicates and sort keys and must reject unsupported expressions explicitly.
Count and existence checks ignore ordering. Lists, streams, and pages apply the sort
keys in order; pages also require a stable order with identity as a tie breaker.

```csharp
using Soroush.Domain.Abstractions;
using Soroush.Persistence.Abstractions;

public sealed class Order : IEntity<Guid>
{
    public Guid Id { get; init; }
    public decimal Total { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class FindOrders(IReadRepository<Order, Guid> orders)
{
    public Task<PagedResult<Order>> Handle(CancellationToken cancellationToken)
    {
        var specification = new Specification<Order>(
            order => order.Total >= 100m,
            SortExpression<Order>.Create(order => order.Total, descending: true));

        return orders.PageAsync(specification, new PageRequest(1, 20), cancellationToken);
    }
}
```

`Specification<T>.All` matches everything. `PageRequest` uses one-based page numbers,
positive page sizes, and an overflow-checked offset. `PagedResult<T>` copies items into
a read-only snapshot and exposes `TotalCount`, `TotalPages`, `HasPreviousPage`, and
`HasNextPage`. An empty result has zero pages. A page beyond the last page may be
empty. Items and count may differ under concurrent writes unless the adapter reads
them from the same snapshot. Applications should enforce their own maximum page size.
Tracking, relationship loading, and soft deletion filters are adapter policies.

### Fluent specifications

`AdvancedQuery<TEntity>` is an immutable fluent implementation of `ISpecification<TEntity>`.
It works with existing repositories and can branch into independent queries without
changing its source. Reference `Soroush.Persistence` for the repository page overload
and `IQueryable` application helpers:

```csharp
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

decimal? minimumTotal = 100m;

var query = AdvancedQuery<Order>.All
    .FilterBy(order => order.Total > 0m)
    .FilterByIf(minimumTotal.HasValue,
        order => order.Total >= minimumTotal.GetValueOrDefault())
    .OrderByDescending(order => order.Total)
    .ThenBy(order => order.Id)
    .PageBy(pageNumber: 2, pageSize: 20);

var result = await orders.PageAsync(query, cancellationToken);
```

Repeated `FilterBy` calls combine using AND. `FilterByAny(first, second, ...)` adds
an OR group inside the existing filters: `existing AND (first OR second)`.
It requires at least one predicate, so an OR group cannot accidentally discard an
existing tenant or authorization filter. Predicates can also contain nested boolean
conditions, collection membership, or navigation expressions supported by the adapter.
`AdvancedQuery<TEntity>.From(specification)` starts from an existing specification.

`OrderBy` and `OrderByDescending` replace previous sort keys; `ThenBy` and
`ThenByDescending` append keys and require a primary ordering. Sort keys preserve
their actual CLR types. `PageBy` uses the existing validated, one-based `PageRequest`;
`WithoutPaging` removes paging. The query is unpaged by default. Assign or chain the
returned value when changing a query or field map.

Repository `ListAsync`, `StreamAsync`, `CountAsync`, and `AnyAsync` continue to use
the specification contract and ignore the query's `Page`. Use `PageAsync(query)` to
apply that page; this overload requires a preceding `PageBy` call. Count and existence
operations also ignore ordering. Repository adapters must retain their existing
stable-order and count-before-paging behavior.

### Explicit field maps for specifications

For lower-level specifications with custom aliases or per-field permissions, map
external field names explicitly. Projected queries above discover DTO names automatically:

```csharp
var fields = new QueryFieldMap<Order>()
    .Map("total", order => order.Total)
    .Map("id", order => order.Id, allowFiltering: false);

var query = AdvancedQuery<Order>.All
    .FilterBy(fields.FilterBy("total", FilterOperator.GreaterThanOrEqual, "100.50"))
    .FilterBy(fields.FilterBy("total", FilterOperator.In, new[] { 150m, 200m, 500m }))
    .OrderBy(fields.OrderBy("total", descending: true))
    .ThenBy(fields.OrderBy("id"))
    .PageBy(1, 20);

var result = await orders.PageAsync(query, cancellationToken);
```

Field names are case-insensitive aliases for typed expressions. Unknown fields,
duplicate names, disallowed filtering or sorting, incompatible operators, and invalid
values throw argument exceptions before execution. Fields may use computed selectors
or navigation expressions; the selector must handle any null navigation required by
the provider or in-memory execution. No raw expression or SQL text is parsed.

| Operators | Accepted fields and values |
| --- | --- |
| `Equal`, `NotEqual` | Typed values or invariant-culture strings; null for nullable fields |
| `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual` | Non-null values; the field type must support the comparison |
| `Contains`, `StartsWith`, `EndsWith` | String fields and non-null string values; null fields do not match |
| `In`, `NotIn` | Non-string collections; values are converted and copied when the filter is created |
| `IsNull`, `IsNotNull` | Nullable fields with no supplied value |

Conversion supports nullable types, standard convertible scalar types, enums, `Guid`,
`DateTime`, `DateTimeOffset`, and `TimeSpan`. A value must already have the field's CLR
type or be a string; numeric values of a different CLR type are rejected to avoid
implicit rounding. Deserialize transport-specific values such as JSON elements into
CLR values or strings first. String matching follows the provider's collation and case
rules. Providers remain responsible for translating expressions and null semantics.
Applications should limit filter counts, collection sizes, and page sizes for external requests.

### Applying queries in adapters

`Soroush.Persistence` provides helpers for adapters that work with `IQueryable<TEntity>`:

```csharp
// source is the adapter's IQueryable<Order>.
var matching = source.ApplyFilters(query); // Count/Any use this query.
var ordered = source.ApplySpecification(query); // Filters + ordering, no paging.
var page = source.ApplyQuery(query, order => order.Id); // Filters + stable order + paging.
```

The helpers build deferred `Queryable` expressions through the original provider.
They do not compile predicates, enumerate results, or depend on EF Core. Materialize
using the provider's own synchronous or asynchronous methods; count `matching` before
paging when constructing a `PagedResult<T>`.

When paging, the `stableKey` overload appends the supplied unique key, or uses it as the
primary ordering when no sort keys exist. Without paging, this key is ignored.
`ApplyQuery(query)` without a stable key requires explicit ordering for paged queries;
include a unique tie breaker yourself in that case. `ApplyOrdering` is also available
when an adapter needs only the sort portion. These helpers do not add ORM tracking,
relationship loading, or soft deletion policies.

### Repository dependency injection

Register the session and unit of work together with repositories discovered in your
adapter assembly:

```csharp
using Soroush.Persistence;

services.AddSoroushPersistence<MyPersistenceSession>(typeof(OrderRepository).Assembly)
    .AddSoroushConnectionFactory<MyConnectionFactory>()
    .AddSaveChangesInterceptor<AuditInterceptor>()
    .AddTransactionInterceptor<TransactionLogInterceptor>();
```

The scan registers closed, concrete implementations under `IRepository<TEntity, TKey>`
and `IReadRepository<TEntity, TKey>`. It ignores abstract and open generic types and
deduplicates assemblies and repeated registrations. It preserves existing closed
registrations. Multiple implementations for an unregistered contract cause an explicit
error before the scan adds services; register the intended contract before scanning.
Application-specific repository interfaces and constructor dependencies such as database
contexts, connection options, and serializers need their own registrations.

For explicit registration without scanning:

```csharp
services.AddSoroushPersistence<MyPersistenceSession>()
    .AddSoroushRepository<Order, Guid, OrderRepository>();
```

Inject `IRepository<Order, Guid>` for reads and staged writes, or
`IReadRepository<Order, Guid>` for reads. The read contract resolves to a scoped,
non-owning view over the same write repository, so both contracts observe the same
repository state and a disposable repository is disposed once. The concrete repository
type is not registered as another service. Repositories borrow the shared session and
must not dispose it. Inject `MyPersistenceSession` in repository constructors to share
the unit of work's adapter.

`AddSoroushReadRepository<Order, Guid, OrderReadRepository>()` registers a dedicated
read adapter without adding write access. Call it before the writable registration
when you want separate adapters for the read and write contracts. All helpers preserve
existing registrations for the exact service type.

Open generic adapters can be registered explicitly:

```csharp
services.AddSoroushRepository(typeof(MyRepository<,>));
// For a read-only default instead:
// services.AddSoroushReadRepository(typeof(MyReadRepository<,>));
```

The generic parameters must be entity followed by key, with constraints compatible
with the repository contracts. Closed registrations override the generic fallback for
their entity. Repositories and connection factories are scoped; the caller owns each
connection returned by `IDbConnectionFactory`. No provider or connection is created
during registration. `AddSoroushUnitOfWork<TSession>()` remains available when only
the session and coordinator are needed.

### Saving and transactions

`IRepository` stages adds, updates, and removals; it never commits them implicitly.
Repositories participating in one unit of work must share the same persistence session.
The application owns that session's lifetime. An explicit transaction is caller-owned:

```csharp
await using var transaction = await unitOfWork.BeginTransactionAsync(
    cancellationToken: cancellationToken);
await repository.AddAsync(order, cancellationToken);
await unitOfWork.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

Commit does not call save automatically. Disposing an active transaction rolls it back.
Commit and rollback are terminal operations. Adapters must explicitly reject unsupported
isolation levels and nested transactions. Domain event dispatch is application-controlled;
events have no dependency on mediator notifications. Reliable delivery requires an
application or adapter strategy such as an outbox.

### Unit of work runtime and interceptors

Reference `Soroush.Persistence` for the coordinator. Implement `IPersistenceSession`
with raw save and begin-transaction operations against your provider; returned
`ITransaction` handles must expose `IsCompleted` and release their resources on disposal.
The coordinator owns transaction handles and borrows the session. Dispose the coordinator
before disposing the session. Outside DI, construct it with
`new UnitOfWork(session, saveInterceptors, transactionInterceptors)` and use `await using`.

Register the concrete adapter and coordinator as scoped services:

```csharp
using Soroush.Persistence;

services.AddSoroushUnitOfWork<MyPersistenceSession>()
    .AddSaveChangesInterceptor<AuditInterceptor>()
    .AddTransactionInterceptor<TransactionLogInterceptor>();
```

Repositories and interceptors can inject `MyPersistenceSession` to access the same
scoped adapter. The helper registers the concrete adapter and `IUnitOfWork`; it does
not add an `IPersistenceSession` alias. This prevents registering the same disposable
session twice. The DI scope disposes the coordinator before its adapter. Registrations
are additive, preserve existing registrations, and deduplicate each interceptor type.
Dispose scopes asynchronously when using the unit of work.

For automatic transaction management, call:

```csharp
await unitOfWork.ExecuteInTransactionAsync(async cancellationToken =>
{
    await repository.AddAsync(order, cancellationToken);
}, cancellationToken: cancellationToken);
```

This begins a transaction, runs the callback once, saves pending changes, commits, and
disposes the handle. A generic overload returns the callback's result. It never retries
application work or joins an existing transaction. Cancellation during begin skips the
callback and cleans up the transaction. The callback must not commit, roll
back, dispose, or replace the managed transaction. `CurrentTransaction` exposes the
active handle; it becomes null after a successful commit or rollback. Concurrent and
reentrant persistence operations are rejected. Use one unit of work per logical operation.

Derive from `SaveChangesInterceptor` or `TransactionInterceptor` and override the hooks
you need. They provide no-op defaults for the corresponding interfaces:

| Interceptor | Before hooks | After hooks | Failure hook |
| --- | --- | --- | --- |
| `ISaveChangesInterceptor` | `SavingChangesAsync` | `SavedChangesAsync` (with affected count) | `SaveChangesFailedAsync` |
| `ITransactionInterceptor` | `TransactionStartingAsync`, `TransactionCommittingAsync`, `TransactionRollingBackAsync` | `TransactionStartedAsync`, `TransactionCommittedAsync`, `TransactionRolledBackAsync` | `TransactionFailedAsync` (context identifies begin, commit, or rollback) |

Hooks run sequentially in registration order. Normal hooks stop on the first failure;
failure hooks all run, including when cancellation caused the failure. Before-save hooks
can apply auditing or prepare tracked entities through an injected adapter. Hooks must
not call save, begin, commit, or rollback recursively. After-save means changes were
flushed; an active transaction has not yet committed. After-commit means the provider
has committed. An after-hook exception propagates without reporting that provider
operation as failed; in particular, an after-commit exception never triggers rollback.
If an after-begin hook fails, the coordinator cleans up the unreturned transaction.

Managed execution uses an uncanceled token for rollback and disposal after failure or
cancellation. Disposal still attempts provider rollback if a before-rollback hook throws,
and always attempts to release the handle. The primary exception and its stack are
preserved; secondary observer and cleanup exceptions are available as `List<Exception>`
in `exception.Data["Soroush.Persistence.SecondaryExceptions"]`.

A provider save, begin, commit, rollback, or disposal failure faults the coordinator.
Rollback and disposal remain available; further saves, begins, and commits require a new
scope. The coordinator honors the provider handle's `IsCompleted` state, preventing
rollback of committed work if subsequent adapter cleanup throws. A failed commit can
have an unknown database outcome, so the coordinator never
retries it. Rollback does not restore in-memory tracked entity state. Discard the session
after a failed managed operation. These hooks do not implement durable event delivery.

For SQL and micro-ORM adapters, `IDbConnectionFactory.OpenConnectionAsync` returns a
new, open `DbConnection` owned by its caller. Dispose it after use. The factory cleans
up a partially created connection if opening fails or is canceled.

Pack an individual library with, for example,
`dotnet pack src/Soroush.Persistence.Abstractions -c Release`.

## Mediator abstractions

`Soroush.Mediator.Abstractions` targets .NET Standard 2.0 and .NET 8. The .NET Standard asset uses
`Microsoft.Bcl.AsyncInterfaces`, while .NET 8 uses the built-in async interfaces.
It provides request,
command and query contracts and their handlers, including streaming queries,
`ISender`, and a request pipeline behavior contract. Responses are not tied to
an application-specific result type. Projected queries and their base handlers also live in this package and depend on `Soroush.Persistence` for query application. These are Soroush contracts;
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
tag such as `v0.1.1` to run the tests, build all packages with that version,
and publish them to NuGet.org:

```sh
git tag v0.1.1
git push origin v0.1.1
```

Use a new tag and version for each release. Existing package versions are skipped
on a retry; NuGet does not allow replacing an already published version. A failed
release can be retried from the Actions page after fixing the problem. Prerelease
versions such as `v0.2.0-beta.1` are supported. Package publication may take time to
appear on NuGet.org.
