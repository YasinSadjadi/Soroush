using Soroush.Mediator.Abstractions;
using System.Collections;
using System.Linq.Expressions;
using Soroush.Domain.Abstractions;

namespace Soroush.Abstractions.Tests;

public class AdvancedQueryTests
{
    private static readonly Order[] Orders =
    {
        new Order { Id = 4, Tenant = 1, Total = 200m, Name = "other" },
        new Order { Id = 2, Tenant = 1, Total = 100m, Name = "priority" },
        new Order { Id = 5, Tenant = 2, Total = 300m, Name = "priority" },
        new Order { Id = 3, Tenant = 1, Total = 200m, Name = "priority" },
        new Order { Id = 1, Tenant = 1, Total = 50m, Name = null }
    };

    [Fact]
    public void FiltersAndOrGroupsPreserveTheScopeOfExistingPredicates()
    {
        var query = AdvancedQuery<Order>.All
            .FilterBy(order => order.Tenant == 1)
            .FilterByAny(order => order.Total >= 200m, order => order.Name == "priority")
            .FilterBy(order => order.Total <= 200m)
            .OrderBy(order => order.Id);

        Assert.Equal(new[] { 2, 3, 4 }, Orders.AsQueryable().ApplyQuery(query).Select(order => order.Id));
        Assert.Equal(5, Orders.AsQueryable().ApplyQuery(AdvancedQuery<Order>.All).Count());
    }

    [Fact]
    public void ConditionalFiltersAndNullGuardsShortCircuit()
    {
        var query = new AdvancedQuery<Order>()
            .FilterByIf(false, order => order.Total > 1000m)
            .FilterByIf(true, order => order.Name != null)
            .FilterBy(order => order.Name!.StartsWith("p"));

        Assert.Equal(new[] { 2, 5, 3 }, Orders.AsQueryable().ApplyQuery(query).Select(order => order.Id));
    }

    [Fact]
    public void OrderingUsesTypedKeysAndOrderByReplacesPreviousKeys()
    {
        var query = new AdvancedQuery<Order>().OrderByDescending(order => order.Total).ThenBy(order => order.Id);
        Assert.Equal(new[] { 5, 3, 4, 2, 1 }, Orders.AsQueryable().ApplyQuery(query).Select(order => order.Id));
        Assert.Equal(typeof(decimal), query.Orderings[0].KeySelector.ReturnType);
        Assert.Equal(typeof(int), query.Orderings[1].KeySelector.ReturnType);

        var reset = query.OrderBy(order => order.Tenant).ThenByDescending(order => order.Id);
        Assert.Equal(2, reset.Orderings.Count);
        Assert.Equal(new[] { 4, 3, 2, 1, 5 }, Orders.AsQueryable().ApplyQuery(reset).Select(order => order.Id));
    }

    [Fact]
    public void QueryBranchesPreserveFiltersOrderingAndPagingWithoutChangingTheirSource()
    {
        var original = AdvancedQuery<Order>.All.FilterBy(order => order.Tenant == 1).OrderBy(order => order.Id);
        var page = original.PageBy(2, 2);
        var branch = page.FilterBy(order => order.Total >= 100m).OrderByDescending(order => order.Total);

        Assert.Null(original.Page);
        Assert.Null(AdvancedQuery<Order>.All.Page);
        Assert.Empty(AdvancedQuery<Order>.All.Orderings);
        Assert.Equal(new[] { 3, 4 }, Orders.AsQueryable().ApplyQuery(page).Select(order => order.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Orders.AsQueryable().ApplyQuery(original).Select(order => order.Id));
        Assert.Equal(new[] { 4, 3, 2 }, Orders.AsQueryable().ApplyQuery(branch.WithoutPaging()).Select(order => order.Id));
        Assert.Same(page.Page, branch.Page);
        Assert.Throws<NotSupportedException>(() => ((IList<SortExpression<Order>>)branch.Orderings).Clear());
    }

    [Fact]
    public void ExistingSpecificationsCanBeUsedAsAQueryStartingPoint()
    {
        var specification = new Specification<Order>(order => order.Tenant == 1,
            SortExpression<Order>.Create(order => order.Id));
        var query = AdvancedQuery<Order>.From(specification).FilterBy(order => order.Total >= 200m);

        Assert.Equal(new[] { 3, 4 }, Orders.AsQueryable().ApplyQuery(query).Select(order => order.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Orders.AsQueryable().ApplySpecification(specification).Select(order => order.Id));
    }

    [Fact]
    public void StableKeyBreaksTiesBeforePagingAndSuppliesADefaultOrder()
    {
        var query = new AdvancedQuery<Order>().FilterBy(order => order.Tenant == 1)
            .OrderByDescending(order => order.Total).PageBy(2, 1);

        Assert.Equal(4, Assert.Single(Orders.AsQueryable().ApplyQuery(query, order => order.Id)).Id);
        var defaultOrder = new AdvancedQuery<Order>().PageBy(2, 2);
        Assert.Equal(new[] { 3, 4 }, Orders.AsQueryable().ApplyQuery(defaultOrder, order => order.Id).Select(order => order.Id));
        Assert.Equal(Orders, Orders.AsQueryable().ApplyQuery(new AdvancedQuery<Order>(), order => order.Id));
    }

    [Fact]
    public void SpecificationsAndCountsExcludePaging()
    {
        var query = new AdvancedQuery<Order>().FilterBy(order => order.Tenant == 1)
            .OrderBy(order => order.Id).PageBy(2, 2);

        Assert.Equal(4, Orders.AsQueryable().ApplyFilters(query).Count());
        Assert.Equal(new[] { 4, 2, 3, 1 }, Orders.AsQueryable().ApplyFilters(query).Select(order => order.Id));
        Assert.Equal(4, Orders.AsQueryable().ApplySpecification(query).Count());
        Assert.Equal(new[] { 3, 4 }, Orders.AsQueryable().ApplyQuery(query).Select(order => order.Id));
        Assert.Empty(Orders.AsQueryable().ApplyQuery(query.PageBy(100, 2)));
    }

    [Fact]
    public void QueryApplicationPreservesProviderAndBuildsDeferredQueryableCalls()
    {
        var source = new RecordedQueryable<Order>();
        var query = new AdvancedQuery<Order>().FilterBy(order => order.Total > 100m)
            .FilterByAny(order => order.Tenant == 1, order => order.Name == "priority")
            .OrderByDescending(order => order.Total).ThenByDescending(order => order.Name).PageBy(2, 2);

        var result = source.ApplyQuery(query, order => order.Id);
        Assert.Same(source.Provider, result.Provider);
        var methods = new List<MethodCallExpression>();
        var expression = result.Expression;
        while (expression is MethodCallExpression method)
        {
            methods.Add(method);
            Assert.Equal(typeof(Queryable), method.Method.DeclaringType);
            expression = method.Arguments[0];
        }
        Assert.Equal(new[] { "Take", "Skip", "ThenBy", "ThenByDescending", "OrderByDescending", "Where" },
            methods.Select(method => method.Method.Name));
        Assert.Equal(typeof(decimal), methods[4].Method.GetGenericArguments()[1]);
        var inspector = new InvocationInspector();
        inspector.Visit(result.Expression);
        Assert.False(inspector.Found);
    }

    [Fact]
    public void InvalidInputsAndUnorderedPagingAreRejected()
    {
        var query = new AdvancedQuery<Order>();
        var source = Orders.AsQueryable();
        Assert.Throws<ArgumentNullException>(() => AdvancedQuery<Order>.From(null!));
        Assert.Throws<ArgumentNullException>(() => query.FilterBy(null!));
        Assert.Throws<ArgumentNullException>(() => query.FilterByIf(false, null!));
        Assert.Throws<ArgumentNullException>(() => query.FilterByAny(null!));
        Assert.Throws<ArgumentException>(() => query.FilterByAny());
        Assert.Throws<ArgumentException>(() => query.FilterByAny(order => true, null!));
        Assert.Throws<ArgumentNullException>(() => query.OrderBy((SortExpression<Order>)null!));
        Assert.Throws<ArgumentNullException>(() => query.ThenBy((SortExpression<Order>)null!));
        Assert.Throws<ArgumentNullException>(() => query.OrderBy<int>(null!));
        Assert.Throws<ArgumentNullException>(() => query.ThenBy<int>(null!));
        Assert.Throws<InvalidOperationException>(() => query.ThenBy(order => order.Id));
        Assert.Throws<ArgumentNullException>(() => query.PageBy(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.PageBy(0, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.PageBy(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.PageBy(int.MaxValue, 2));
        Assert.Throws<InvalidOperationException>(() => source.ApplyQuery(query.PageBy()));
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery((AdvancedQuery<Order>)null!));
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery(query, (Expression<Func<Order, int>>)null!));
        Assert.Throws<ArgumentNullException>(() => ((IQueryable<Order>)null!).ApplyQuery(query));
        Assert.Throws<ArgumentNullException>(() => source.ApplyFilters(null!));
        Assert.Throws<ArgumentNullException>(() => ((IQueryable<Order>)null!).ApplyFilters(query));
        Assert.Throws<ArgumentNullException>(() => source.ApplyOrdering(null!));
        Assert.Throws<ArgumentNullException>(() => ((IQueryable<Order>)null!).ApplyOrdering(query));
    }

    private sealed class Order : IEntity<int>
    {
        public int Id { get; init; }
        public int Tenant { get; init; }
        public decimal Total { get; init; }
        public string? Name { get; init; }
    }

    private sealed class InvocationInspector : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Found = true;
            return base.VisitInvocation(node);
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
