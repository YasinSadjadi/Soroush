using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Soroush.Mediator;
using Soroush.Mediator.Abstractions;

namespace Soroush.Abstractions.Tests;

public class ProjectedQueryTests
{
    private static readonly Product[] Products =
    {
        new() { Id = 4, TenantId = 1, UnitPrice = 25m, Quantity = 2, Name = "four", Customer = new Customer { Name = null } },
        new() { Id = 2, TenantId = 1, UnitPrice = 30m, Quantity = 4, Name = "two", Customer = new Customer { Name = "Z" } },
        new() { Id = 5, TenantId = 2, UnitPrice = 999m, Quantity = 1, Name = "other tenant", Customer = new Customer { Name = "X" } },
        new() { Id = 3, TenantId = 1, UnitPrice = 60m, Quantity = 2, Name = "three", Customer = new Customer { Name = "A" } },
        new() { Id = 1, TenantId = 1, UnitPrice = 5m, Quantity = 3, Name = "one", Customer = new Customer { Name = "B" } }
    };

    [Fact]
    public void DirectQueryAppliesNamesToRenamedComputedAndNavigationOutputFields()
    {
        var handler = new ProductsQueryHandler(new TestQueryContext());
        var request = new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("amount", FilterOperator.GreaterThanOrEqual, "100") },
            OrderBy = new[] { new QuerySort("customerName") },
            PageSize = 1
        };

        var result = handler.ApplyTo(Products.AsQueryable().Where(product => product.TenantId == 1), request).Single();

        Assert.Equal(3, result.Id);
        Assert.Equal(120m, result.Amount);
        Assert.Equal("three", result.DisplayName);
        Assert.Equal("A", result.CustomerName);
        Assert.Throws<ArgumentException>(() => handler.ApplyTo(Products.AsQueryable(), new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("UnitPrice", FilterOperator.Equal, "60") }
        }));
        Assert.Throws<ArgumentException>(() => handler.ApplyTo(Products.AsQueryable(), new ProductsQuery
        {
            OrderBy = new[] { new QuerySort("TenantId") }
        }));
    }

    [Fact]
    public void ExistingSelectCanUseRequestOptionsWithoutAManualFieldMap()
    {
        var selected = Products.AsQueryable().Select(product => new
        {
            product.Id,
            Caption = product.Name,
            Amount = product.UnitPrice * product.Quantity
        });
        var options = new QueryOptions
        {
            FilterBy = new[] { new QueryFilter("Caption", FilterOperator.StartsWith, "t") },
            OrderBy = new[] { new QuerySort("amount", descending: true) }
        };

        Assert.Equal(new[] { 2, 3 }, selected.ApplyQuery(options).Select(row => row.Id));
        Assert.Equal(5, selected.Count());
    }

    [Fact]
    public void DefaultIdentityOrderingAndTieBreakersMakePagesRepeatable()
    {
        var handler = new ProductsQueryHandler(new TestQueryContext());
        var source = Products.AsQueryable().Where(product => product.TenantId == 1);
        Assert.Equal(new[] { 1, 2, 3, 4 }, handler.ApplyTo(source, new ProductsQuery()).Select(row => row.Id));

        var request = new ProductsQuery
        {
            OrderBy = new[] { new QuerySort("Amount", descending: true) },
            PageNumber = 2,
            PageSize = 1
        };
        Assert.Equal(3, handler.ApplyTo(source, request).Single().Id);

        request.OrderBy = new[] { new QuerySort("Id", descending: true) };
        Assert.Equal(3, handler.ApplyTo(source, request).Single().Id);
        Assert.Single(request.ToQuery<ProductDto>().Orderings);
    }

    [Fact]
    public void OrFiltersCannotEscapeSourceRestrictions()
    {
        var handler = new ProductsQueryHandler(new TestQueryContext());
        var request = new ProductsQuery
        {
            Logic = FilterLogic.Or,
            FilterBy = new[]
            {
                new QueryFilter("Amount", FilterOperator.GreaterThan, "900"),
                new QueryFilter("DisplayName", FilterOperator.Equal, "two")
            }
        };

        Assert.Equal(2, handler.ApplyTo(Products.AsQueryable().Where(product => product.TenantId == 1), request).Single().Id);
        request.FilterBy = Array.Empty<QueryFilter>();
        Assert.Equal(4, handler.ApplyTo(Products.AsQueryable().Where(product => product.TenantId == 1), request).Count());
    }

    [Fact]
    public async Task MediatorDiscoversConcreteHandlerAndRunsProjectionFiltersCountOrderingAndPage()
    {
        var services = new ServiceCollection();
        services.AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        Assert.IsType<ProductsQueryHandler>(scope.ServiceProvider.GetRequiredService<IRequestHandler<ProductsQuery, PagedResult<ProductDto>>>());
        var context = Assert.IsType<TestQueryContext>(scope.ServiceProvider.GetRequiredService<IQueryContext>());
        var request = new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("Amount", FilterOperator.GreaterThanOrEqual, "100") },
            OrderBy = new[] { new QuerySort("Amount", descending: true) },
            PageNumber = 2,
            PageSize = 1
        };
        using var cancellation = new CancellationTokenSource();

        var result = await sender.Send(request, cancellation.Token);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(3, Assert.Single(result.Items).Id);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(new[] { "source", "count", "list" }, context.Calls);
        Assert.Equal(cancellation.Token, context.LastToken);
        Assert.Equal(new[] { "Where", "Select", "Where" }, QueryMethods(context.CountExpression!));
        Assert.Equal(new[] { "Take", "Skip", "ThenBy", "OrderByDescending", "Where", "Select", "Where" }, QueryMethods(context.ListExpression!));
    }

    [Fact]
    public async Task BaseQueryHandlersRunTheNormalMediatorPipeline()
    {
        var services = new ServiceCollection();
        var behavior = new ProductsBehavior();
        services.AddSingleton<IPipelineBehavior<ProductsQuery, PagedResult<ProductDto>>>(behavior);
        services.AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProductsQuery());

        Assert.True(behavior.Called);
        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public async Task CancellationAndInvalidRequestsStopBeforeDatabaseAccess()
    {
        var services = new ServiceCollection().AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var context = Assert.IsType<TestQueryContext>(scope.ServiceProvider.GetRequiredService<IQueryContext>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.Send(new ProductsQuery(), new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => sender.Send(new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("secret", FilterOperator.Equal, "1") }
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sender.Send(new ProductsQuery { PageSize = 0 }));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task CancellationAfterCountStopsListExecutionAndCountFailuresPropagate()
    {
        var context = new TestQueryContext();
        var services = new ServiceCollection();
        services.AddSingleton<IQueryContext>(context);
        services.AddSoroushQueries(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var cancellation = new CancellationTokenSource();
        context.AfterCount = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.Send(new ProductsQuery(), cancellation.Token));
        Assert.Equal(new[] { "source", "count" }, context.Calls);

        context.Calls.Clear();
        var failure = new InvalidOperationException("database failed");
        context.AfterCount = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new ProductsQuery())));
        Assert.Equal(new[] { "source", "count" }, context.Calls);
    }

    [Fact]
    public async Task RequestChangesWhileCountingDoNotChangeThePreparedQuery()
    {
        var request = new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("Amount", FilterOperator.GreaterThan, "100") },
            PageSize = 1
        };
        var context = new TestQueryContext
        {
            AfterCount = () =>
            {
                request.FilterBy[0].Value = "10000";
                request.PageNumber = 100;
                request.PageSize = 100;
            }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IQueryContext>(context);
        services.AddSoroushQueries(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.PageSize);
        Assert.Equal(1, result.Page.PageNumber);
    }

    [Fact]
    public async Task RegistrationDeduplicatesConcreteHandlersAndFindsInheritedHandlerContracts()
    {
        var services = new ServiceCollection();
        var context = new TestQueryContext();
        services.AddSingleton<IQueryContext>(context);
        services.AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly, typeof(ProductsQuery).Assembly)
            .AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        Assert.Single(scope.ServiceProvider.GetServices<IRequestHandler<ProductsQuery, PagedResult<ProductDto>>>());
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IQueryContext>());
        Assert.Equal(4, (await sender.Send(new InheritedProductsQuery())).TotalCount);
        context.Calls.Clear();
        Assert.Equal("manual", Assert.Single((await sender.Send(new ManualProductsQuery())).Items).DisplayName);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public void RequestsBindFromJsonAndUseOnlyOutputFieldNames()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var request = JsonSerializer.Deserialize<ProductsQuery>("""
            {
              "filterBy": [{ "field": "Amount", "operator": "In", "values": ["50", "120"] }],
              "orderBy": [{ "field": "DisplayName", "descending": true }],
              "pageNumber": 1,
              "pageSize": 2
            }
            """, jsonOptions)!;

        var handler = new ProductsQueryHandler(new TestQueryContext());
        Assert.Equal(new[] { 2, 3 }, handler.ApplyTo(Products.AsQueryable(), request).Select(row => row.Id));
        Assert.DoesNotContain("Mapping", JsonSerializer.Serialize(request));
    }

    [Fact]
    public void ConstructorProjectionsWithoutIdentityUseAnExplicitNamedOrder()
    {
        var handler = new SummaryQueryHandler(new TestQueryContext());
        var query = new SummaryQuery { OrderBy = new[] { new QuerySort("Amount") }, PageSize = 1 };
        Assert.Equal("one", handler.ApplyTo(Products.AsQueryable(), query).Single().Name);
        Assert.Throws<ArgumentException>(() => handler.ApplyTo(Products.AsQueryable(), new SummaryQuery()));
    }

    [Fact]
    public void QueryCompositionRemainsDeferredAndPreservesTheOriginalProvider()
    {
        var handler = new ProductsQueryHandler(new TestQueryContext());
        var source = new RecordedQueryable<Product>();
        var query = new ProductsQuery
        {
            FilterBy = new[] { new QueryFilter("CustomerName", FilterOperator.IsNotNull) },
            OrderBy = new[] { new QuerySort("Amount", descending: true) }
        };
        var result = handler.ApplyTo(source, query);

        Assert.Same(source.Provider, result.Provider);
        Assert.Equal(new[] { "Take", "Skip", "ThenBy", "OrderByDescending", "Where", "Select" }, QueryMethods(result.Expression));
        var inspector = new ForbiddenCallInspector();
        inspector.Visit(result.Expression);
        Assert.False(inspector.Found);
    }

    [Fact]
    public void InvalidOptionsAndRegistrationsFailBeforeMutationOrExecution()
    {
        var handler = new ProductsQueryHandler(new TestQueryContext());
        var source = Products.AsQueryable().Select(product => new ProductDto { Id = product.Id });
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery((QueryOptions)null!));
        Assert.Throws<ArgumentNullException>(() => ((IQueryable<ProductDto>)null!).ApplyQuery(new QueryOptions()));
        Assert.Throws<ArgumentNullException>(() => new ProductsQueryHandler(null!));
        Assert.Throws<ArgumentNullException>(() => handler.ApplyTo(null!, new ProductsQuery()));
        Assert.Throws<ArgumentNullException>(() => handler.ApplyTo(Products.AsQueryable(), null!));
        Assert.Throws<InvalidOperationException>(() => new NullMappingHandler(new TestQueryContext()).ApplyTo(Products.AsQueryable(), new NullMappingQuery()));
        Assert.Throws<ArgumentException>(() => new QueryOptions { FilterBy = null! }.ToQuery<ProductDto>());
        Assert.Throws<ArgumentException>(() => new QueryOptions { OrderBy = null! }.ToQuery<ProductDto>());
        Assert.Throws<ArgumentException>(() => new QueryOptions { FilterBy = new QueryFilter[] { null! } }.ToQuery<ProductDto>());
        Assert.Throws<ArgumentException>(() => new QueryOptions { OrderBy = new QuerySort[] { null! } }.ToQuery<ProductDto>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryOptions { Logic = (FilterLogic)99 }.ToQuery<ProductDto>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryOptions { PageNumber = int.MaxValue, PageSize = 2 }.ToQuery<ProductDto>());
        foreach (var filter in new[]
        {
            new QueryFilter("Amount", FilterOperator.In, "1"),
            new QueryFilter("Amount", FilterOperator.Equal) { Values = new[] { "1" } },
            new QueryFilter("Amount", FilterOperator.In) { Values = null! }
        })
            Assert.Throws<ArgumentException>(() => new QueryOptions { FilterBy = new[] { filter } }.ToQuery<ProductDto>());

        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddSoroushQueries<TestQueryContext>(null!));
        Assert.Throws<ArgumentException>(() => services.AddSoroushQueries<TestQueryContext>(typeof(ProductsQuery).Assembly, null!));
        Assert.Empty(services);
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddSoroushQueries<TestQueryContext>());
    }

    [Fact]
    public async Task QueryRequestsWithoutConcreteHandlersAreNotAutomaticallyRegistered()
    {
        var services = new ServiceCollection().AddSoroushQueries<TestQueryContext>(typeof(ProductsQueryHandler).Assembly);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = Assert.IsType<TestQueryContext>(scope.ServiceProvider.GetRequiredService<IQueryContext>());

        Assert.Null(scope.ServiceProvider.GetService<IRequestHandler<UnhandledProductsQuery, PagedResult<ProductDto>>>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new UnhandledProductsQuery()));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task HandlerCanRestrictTheEntitySourceUsingRequestInputsBeforeProjection()
    {
        var context = new TestQueryContext();
        var handler = new InheritedProductsHandler(context);
        var request = new InheritedProductsQuery
        {
            MinimumId = 3,
            Logic = FilterLogic.Or,
            FilterBy = new[]
            {
                new QueryFilter("Id", FilterOperator.Equal, "1"),
                new QueryFilter("Id", FilterOperator.GreaterThanOrEqual, "3")
            }
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { 3, 4 }, result.Items.Select(row => row.Id));
        Assert.Equal(new[] { "Where", "Select", "Where", "Where" }, QueryMethods(context.CountExpression!));
    }

    [Fact]
    public async Task BaseHandlerRejectsNullRequestsBeforeAccessingTheSource()
    {
        var context = new TestQueryContext();
        var handler = new ProductsQueryHandler(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.Handle(null!, CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    private static string[] QueryMethods(Expression expression)
    {
        var methods = new List<string>();
        while (expression is MethodCallExpression call)
        {
            methods.Add(call.Method.Name);
            Assert.Equal(typeof(Queryable), call.Method.DeclaringType);
            expression = call.Arguments[0];
        }
        return methods.ToArray();
    }

    public sealed class Product
    {
        public int Id { get; init; }
        public int TenantId { get; init; }
        public decimal UnitPrice { get; init; }
        public int Quantity { get; init; }
        public string Name { get; init; } = string.Empty;
        public Customer Customer { get; init; } = new();
    }

    public sealed class Customer { public string? Name { get; init; } }

    public sealed class ProductDto
    {
        public int Id { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public string? CustomerName { get; init; }
    }

    public record Summary(string Name, decimal Amount);

    public sealed class ProductsQuery : ProjectedQuery<ProductDto>;

    public sealed class ProductsQueryHandler : ProjectedQueryHandler<ProductsQuery, Product, ProductDto>
    {
        public ProductsQueryHandler(IQueryContext context) : base(context) { }

        protected override void ConfigureFilterableFields(QueryFieldRegistry<ProductDto> fields)
            => fields.Add(row => row.Id).Add(row => row.Amount).Add(row => row.DisplayName).Add(row => row.CustomerName);

        protected override void ConfigureOrderableFields(QueryFieldRegistry<ProductDto> fields)
            => fields.Add(row => row.Id).Add(row => row.Amount).Add(row => row.DisplayName).Add(row => row.CustomerName);

        protected override Expression<Func<Product, ProductDto>> Mapping => product => new ProductDto
        {
            Id = product.Id,
            DisplayName = product.Name,
            Amount = product.UnitPrice * product.Quantity,
            CustomerName = product.Customer.Name
        };
    }

    public abstract class ProductsQueryBase : ProjectedQuery<ProductDto>;

    public abstract class ProductsHandlerBase<TQuery> : ProjectedQueryHandler<TQuery, Product, ProductDto>
        where TQuery : ProjectedQuery<ProductDto>
    {
        protected ProductsHandlerBase(IQueryContext context) : base(context) { }

        protected override void ConfigureFilterableFields(QueryFieldRegistry<ProductDto> fields) => fields.Add(row => row.Id);

        protected override Expression<Func<Product, ProductDto>> Mapping => product => new ProductDto { Id = product.Id };
    }

    public sealed class InheritedProductsQuery : ProductsQueryBase
    {
        public int MinimumId { get; set; }
    }

    public sealed class InheritedProductsHandler : ProductsHandlerBase<InheritedProductsQuery>
    {
        public InheritedProductsHandler(IQueryContext context) : base(context) { }

        protected override IQueryable<Product> CreateQuery(InheritedProductsQuery request)
            => base.CreateQuery(request).Where(product => product.Id >= request.MinimumId);
    }

    public abstract class OpenQuery<T> : ProjectedQuery<ProductDto>;
    public sealed class UnhandledProductsQuery : ProjectedQuery<ProductDto>;
    public sealed class ManualProductsQuery : ProductsQueryBase;

    public sealed class ManualProductsHandler : IQueryHandler<ManualProductsQuery, PagedResult<ProductDto>>
    {
        public Task<PagedResult<ProductDto>> Handle(ManualProductsQuery request, CancellationToken cancellationToken)
            => Task.FromResult(new PagedResult<ProductDto>(new[] { new ProductDto { DisplayName = "manual" } }, 1, new PageRequest()));
    }

    public sealed class SummaryQuery : ProjectedQuery<Summary>;

    public sealed class SummaryQueryHandler : ProjectedQueryHandler<SummaryQuery, Product, Summary>
    {
        public SummaryQueryHandler(IQueryContext context) : base(context) { }

        protected override void ConfigureOrderableFields(QueryFieldRegistry<Summary> fields) => fields.Add(row => row.Amount);

        protected override Expression<Func<Product, Summary>> Mapping => product => new Summary(product.Name, product.UnitPrice * product.Quantity);
    }

    public sealed class NullMappingQuery : ProjectedQuery<ProductDto>;

    public sealed class NullMappingHandler : ProjectedQueryHandler<NullMappingQuery, Product, ProductDto>
    {
        public NullMappingHandler(IQueryContext context) : base(context) { }

        protected override Expression<Func<Product, ProductDto>> Mapping => null!;
    }

    public sealed class TestQueryContext : IQueryContext
    {
        public List<string> Calls { get; } = new();
        public Expression? CountExpression { get; private set; }
        public Expression? ListExpression { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Action? AfterCount { get; set; }

        public IQueryable<TEntity> Query<TEntity>() where TEntity : class
        {
            Calls.Add("source");
            if (typeof(TEntity) != typeof(Product)) throw new NotSupportedException();
            return (IQueryable<TEntity>)(object)Products.AsQueryable().Where(product => product.TenantId == 1);
        }

        public Task<long> CountAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("count");
            LastToken = cancellationToken;
            CountExpression = query.Expression;
            var count = query.LongCount();
            AfterCount?.Invoke();
            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<TResult>> ListAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("list");
            LastToken = cancellationToken;
            ListExpression = query.Expression;
            return Task.FromResult<IReadOnlyList<TResult>>(query.ToArray());
        }
    }

    private sealed class ProductsBehavior : IPipelineBehavior<ProductsQuery, PagedResult<ProductDto>>
    {
        public bool Called { get; private set; }
        public Task<PagedResult<ProductDto>> Handle(ProductsQuery request, RequestHandlerDelegate<PagedResult<ProductDto>> next, CancellationToken cancellationToken)
        {
            Called = true;
            return next(cancellationToken);
        }
    }

    private sealed class ForbiddenCallInspector : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Found = true;
            return base.VisitInvocation(node);
        }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "Compile" || node.Method.Name == "AsEnumerable" || node.Method.Name == "ToList") Found = true;
            return base.VisitMethodCall(node);
        }
    }

    private sealed class RecordedQueryable<T> : IOrderedQueryable<T>
    {
        public RecordedQueryable()
        {
            Provider = new RecordingProvider();
            Expression = Expression.Constant(this);
        }

        public RecordedQueryable(IQueryProvider provider, Expression expression)
        {
            Provider = provider;
            Expression = expression;
        }

        public Type ElementType => typeof(T);
        public Expression Expression { get; }
        public IQueryProvider Provider { get; }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Query must remain deferred.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingProvider : IQueryProvider
    {
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new RecordedQueryable<TElement>(this, expression);
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public object Execute(Expression expression) => throw new InvalidOperationException("Query must remain deferred.");
        public TResult Execute<TResult>(Expression expression) => throw new InvalidOperationException("Query must remain deferred.");
    }
}
