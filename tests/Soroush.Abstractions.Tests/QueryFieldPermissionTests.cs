using System.Linq.Expressions;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;
using Soroush.Mediator.Abstractions;
using static Soroush.Abstractions.Tests.ProjectedQueryTests;

namespace Soroush.Abstractions.Tests;

public class QueryFieldPermissionTests
{
    [Fact]
    public async Task UnconfiguredHandlersDenyClientFieldsEvenWhenTheyExistOnTheOutputDto()
    {
        var context = new TestQueryContext();
        var handler = new LockedHandler(context);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new LockedQuery
        {
            FilterBy = new[] { new QueryFilter("Amount", FilterOperator.GreaterThan, "100") }
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new LockedQuery
        {
            OrderBy = new[] { new QuerySort("DisplayName") }
        }, CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task ServerDefaultIdentityOrderDoesNotGrantClientPermissionToUseIdentity()
    {
        var context = new TestQueryContext();
        var handler = new LockedHandler(context);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new LockedQuery
        {
            FilterBy = new[] { new QueryFilter("Id", FilterOperator.Equal, "1") }
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new LockedQuery
        {
            OrderBy = new[] { new QuerySort("Id", descending: true) }
        }, CancellationToken.None));
        Assert.Empty(context.Calls);

        var result = await handler.Handle(new LockedQuery(), CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Items.Select(row => row.Id));
        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public async Task FilterableAndOrderablePermissionsAreIndependent()
    {
        var context = new TestQueryContext();
        var handler = new RestrictedHandler(context);

        var result = await handler.Handle(new RestrictedQuery
        {
            FilterBy = new[] { new QueryFilter("amount", FilterOperator.GreaterThanOrEqual, "100") },
            OrderBy = new[] { new QuerySort("displayName") }
        }, CancellationToken.None);

        Assert.Equal(new[] { 3, 2 }, result.Items.Select(row => row.Id));
        context.Calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new RestrictedQuery
        {
            OrderBy = new[] { new QuerySort("Amount") }
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new RestrictedQuery
        {
            FilterBy = new[] { new QueryFilter("DisplayName", FilterOperator.Equal, "two") }
        }, CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    [Theory]
    [InlineData("CustomerName")]
    [InlineData("DisplayName.Length")]
    [InlineData("TenantId")]
    [InlineData("Amount desc; drop table Products")]
    public async Task UnknownOrUnregisteredFieldsRejectTheWholeRequestBeforeDatabaseAccess(string field)
    {
        var context = new TestQueryContext();
        var handler = new RestrictedHandler(context);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new RestrictedQuery
        {
            Logic = FilterLogic.Or,
            FilterBy = new[]
            {
                new QueryFilter("Amount", FilterOperator.GreaterThan, "0"),
                new QueryFilter(field, FilterOperator.Equal, "1")
            }
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new RestrictedQuery
        {
            OrderBy = new[] { new QuerySort("DisplayName"), new QuerySort(field) }
        }, CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task HandlersSharingAnOutputDtoDoNotShareFieldPermissions()
    {
        var restrictedContext = new TestQueryContext();
        var lockedContext = new TestQueryContext();
        var restricted = new RestrictedHandler(restrictedContext);
        var locked = new LockedHandler(lockedContext);
        var filters = new[] { new QueryFilter("Amount", FilterOperator.GreaterThan, "100") };

        Assert.Equal(2, (await restricted.Handle(new RestrictedQuery { FilterBy = filters }, CancellationToken.None)).TotalCount);
        await Assert.ThrowsAsync<ArgumentException>(() => locked.Handle(new LockedQuery { FilterBy = filters }, CancellationToken.None));
        Assert.Empty(lockedContext.Calls);
        Assert.Equal(2, (await restricted.Handle(new RestrictedQuery { FilterBy = filters }, CancellationToken.None)).TotalCount);
    }

    [Fact]
    public void DeferredCompositionEnforcesTheSamePermissionsAsHandle()
    {
        var handler = new RestrictedHandler(new TestQueryContext());
        var source = Array.Empty<Product>().AsQueryable();

        Assert.Throws<ArgumentException>(() => handler.ApplyTo(source, new RestrictedQuery
        {
            OrderBy = new[] { new QuerySort("Amount") }
        }));
        Assert.Throws<ArgumentException>(() => handler.ApplyTo(source, new RestrictedQuery
        {
            FilterBy = new[] { new QueryFilter("CustomerName", FilterOperator.Contains, "A") }
        }));
    }

    [Fact]
    public async Task ExplicitAliasesAreCaseInsensitiveAndDoNotGrantAccessThroughTheClrName()
    {
        var context = new TestQueryContext();
        var handler = new AliasHandler(context);
        var request = new AliasQuery
        {
            FilterBy = new[] { new QueryFilter("COST", FilterOperator.GreaterThanOrEqual, "100") },
            OrderBy = new[] { new QuerySort("KEY", descending: true) }
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.Equal(new[] { 3, 2 }, result.Items.Select(row => row.Id));
        Assert.Equal(1, CountSortCalls(context.ListExpression!));
        context.Calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new AliasQuery
        {
            FilterBy = new[] { new QueryFilter("Amount", FilterOperator.Equal, "120") }
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new AliasQuery
        {
            OrderBy = new[] { new QuerySort("Id") }
        }, CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task DuplicateRegistrationFailsBeforeAccessingTheSource()
    {
        var context = new TestQueryContext();
        var handler = new DuplicateFieldsHandler(context);

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(new DuplicateFieldsQuery(), CancellationToken.None));
        Assert.Empty(context.Calls);
    }

    [Fact]
    public void RegistriesRequireDirectPublicDtoMembersAndUniqueAliases()
    {
        var fields = new QueryFieldRegistry<ProductDto>();
        Assert.Same(fields, fields.Add(row => row.Amount));
        Assert.Same(fields, fields.Add("name", row => row.DisplayName));
        Assert.Throws<ArgumentException>(() => fields.Add("AMOUNT", row => row.Amount));
        Assert.Throws<ArgumentException>(() => fields.Add(" ", row => row.Amount));
        Assert.Throws<ArgumentNullException>(() => fields.Add<decimal>(null!));
        Assert.Throws<ArgumentException>(() => fields.Add(row => row.DisplayName.Length));
        Assert.Throws<ArgumentException>(() => fields.Add(row => row.DisplayName.Trim()));
        Assert.Throws<ArgumentException>(() => fields.Add<object>(row => row.Id));
        Assert.Throws<ArgumentException>(() => fields.Add(row => DateTime.UtcNow));

        var publicFields = new QueryFieldRegistry<FieldOutput>();
        Assert.Same(publicFields, publicFields.Add(row => row.Id));
    }

    [Fact]
    public void ExistingSelectQueriesCanUseExplicitPermissions()
    {
        var source = new[]
        {
            new ProductDto { Id = 2, Amount = 100m, DisplayName = "B" },
            new ProductDto { Id = 1, Amount = 100m, DisplayName = "A" }
        }.AsQueryable();
        var filters = new QueryFieldMap<ProductDto>().Map("Amount", row => row.Amount, allowSorting: false);
        var sorts = new QueryFieldMap<ProductDto>().Map("DisplayName", row => row.DisplayName, allowFiltering: false);
        var options = new QueryOptions
        {
            FilterBy = new[] { new QueryFilter("Amount", FilterOperator.Equal, "100") },
            OrderBy = new[] { new QuerySort("DisplayName", descending: true) }
        };

        Assert.Equal(new[] { 2, 1 }, source.ApplyQuery(options, filters, sorts).Select(row => row.Id));
        options.OrderBy = new[] { new QuerySort("Amount") };
        Assert.Throws<ArgumentException>(() => source.ApplyQuery(options, filters, sorts));
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery(options, null!, sorts));
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery(options, filters, null!));
        Assert.Throws<ArgumentNullException>(() => source.ApplyQuery(null!, filters, sorts));
        Assert.Throws<ArgumentNullException>(() => ((IQueryable<ProductDto>)null!).ApplyQuery(options, filters, sorts));
    }

    private static int CountSortCalls(Expression expression)
    {
        var count = 0;
        while (expression is MethodCallExpression call)
        {
            if (call.Method.Name.StartsWith("OrderBy", StringComparison.Ordinal)
                || call.Method.Name.StartsWith("ThenBy", StringComparison.Ordinal)) count++;
            expression = call.Arguments[0];
        }
        return count;
    }

    public abstract class PermissionHandler<TQuery> : ProjectedQueryHandler<TQuery, Product, ProductDto>
        where TQuery : ProjectedQuery<ProductDto>
    {
        protected PermissionHandler(IQueryContext context) : base(context) { }

        protected override Expression<Func<Product, ProductDto>> Mapping => product => new ProductDto
        {
            Id = product.Id,
            Amount = product.UnitPrice * product.Quantity,
            DisplayName = product.Name,
            CustomerName = product.Customer.Name
        };
    }

    public sealed class LockedQuery : ProjectedQuery<ProductDto>;
    public sealed class LockedHandler : PermissionHandler<LockedQuery>
    {
        public LockedHandler(IQueryContext context) : base(context) { }
    }

    public sealed class RestrictedQuery : ProjectedQuery<ProductDto>;
    public sealed class RestrictedHandler : PermissionHandler<RestrictedQuery>
    {
        public RestrictedHandler(IQueryContext context) : base(context) { }

        protected override void ConfigureFilterableFields(QueryFieldRegistry<ProductDto> fields) => fields.Add(row => row.Amount);
        protected override void ConfigureOrderableFields(QueryFieldRegistry<ProductDto> fields) => fields.Add(row => row.DisplayName);
    }

    public sealed class AliasQuery : ProjectedQuery<ProductDto>;
    public sealed class AliasHandler : PermissionHandler<AliasQuery>
    {
        public AliasHandler(IQueryContext context) : base(context) { }

        protected override void ConfigureFilterableFields(QueryFieldRegistry<ProductDto> fields) => fields.Add("cost", row => row.Amount);
        protected override void ConfigureOrderableFields(QueryFieldRegistry<ProductDto> fields) => fields.Add("key", row => row.Id);
    }

    public sealed class DuplicateFieldsQuery : ProjectedQuery<ProductDto>;
    public sealed class DuplicateFieldsHandler : PermissionHandler<DuplicateFieldsQuery>
    {
        public DuplicateFieldsHandler(IQueryContext context) : base(context) { }

        protected override void ConfigureFilterableFields(QueryFieldRegistry<ProductDto> fields)
            => fields.Add(row => row.Amount).Add("AMOUNT", row => row.Amount);
    }

    private sealed class FieldOutput
    {
        public int Id = 1;
    }
}
