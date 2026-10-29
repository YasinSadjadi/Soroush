using System.Linq.Expressions;


namespace Soroush.Mediator.Abstractions;

/// <summary>Applies specifications and advanced queries without compiling expressions or materializing data.</summary>
/// <remarks>The underlying query provider remains responsible for translating expressions and rejecting unsupported operations.</remarks>
public static class QueryableExtensions
{
    /// <summary>Applies named filters, ordering, and paging to the current selected output.</summary>
    /// <remarks>This overload permits all public DTO members. For restricted client requests, use a configured
    /// projected query handler or the overload accepting explicit filterable and orderable field maps.</remarks>
    public static IQueryable<TResult> ApplyQuery<TResult>(this IQueryable<TResult> source, QueryOptions options)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (options is null) throw new ArgumentNullException(nameof(options));
        return source.ApplyQuery(options.ToQuery<TResult>());
    }

    /// <summary>Applies named filters, ordering, and paging using separate explicit field permissions.</summary>
    public static IQueryable<TResult> ApplyQuery<TResult>(this IQueryable<TResult> source, QueryOptions options,
        QueryFieldMap<TResult> filterableFields, QueryFieldMap<TResult> orderableFields)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (options is null) throw new ArgumentNullException(nameof(options));
        return source.ApplyQuery(options.ToQuery(filterableFields, orderableFields));
    }

    /// <summary>Applies only the predicate, suitable for count and existence queries.</summary>
    public static IQueryable<TEntity> ApplyFilters<TEntity>(this IQueryable<TEntity> source, ISpecification<TEntity> specification)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (specification is null) throw new ArgumentNullException(nameof(specification));
        return source.Where(specification.Criteria);
    }

    /// <summary>Applies primary and subsequent sort keys, preserving each key's actual type.</summary>
    public static IQueryable<TEntity> ApplyOrdering<TEntity>(this IQueryable<TEntity> source, ISpecification<TEntity> specification)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (specification is null) throw new ArgumentNullException(nameof(specification));
        for (var index = 0; index < specification.Orderings.Count; index++)
            source = ApplySort(source, specification.Orderings[index], thenBy: index > 0);
        return source;
    }

    /// <summary>Applies filters and ordering without paging, including when the specification is an advanced query.</summary>
    public static IQueryable<TEntity> ApplySpecification<TEntity>(this IQueryable<TEntity> source, ISpecification<TEntity> specification)
        => source.ApplyFilters(specification).ApplyOrdering(specification);

    /// <summary>Applies filters, ordering, and optional paging. Paging requires an explicit ordering.</summary>
    /// <remarks>The supplied ordering must include a unique tie breaker for stable pages.
    /// Use the stableKey overload to append a unique key automatically.</remarks>
    public static IQueryable<TEntity> ApplyQuery<TEntity>(this IQueryable<TEntity> source, AdvancedQuery<TEntity> query)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Page is not null && query.Orderings.Count == 0)
            throw new InvalidOperationException("Paging requires an ordering. Call OrderBy or supply a unique stable key.");
        return ApplyPage(source.ApplySpecification(query), query.Page);
    }

    /// <summary>Applies a query, appending the supplied unique key when paging to make the order stable.</summary>
    /// <remarks>When no explicit ordering exists, the unique key is the primary sort key.
    /// Without paging, the key is ignored. The caller must supply a key unique within the result.</remarks>
    public static IQueryable<TEntity> ApplyQuery<TEntity, TKey>(this IQueryable<TEntity> source,
        AdvancedQuery<TEntity> query, Expression<Func<TEntity, TKey>> stableKey)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (stableKey is null) throw new ArgumentNullException(nameof(stableKey));
        var result = source.ApplySpecification(query);
        if (query.Page is not null)
            result = ApplySort(result, SortExpression<TEntity>.Create(stableKey), thenBy: query.Orderings.Count > 0);
        return ApplyPage(result, query.Page);
    }

    private static IQueryable<TEntity> ApplyPage<TEntity>(IQueryable<TEntity> source, PageRequest? page)
        => page is null ? source : source.Skip(page.Offset).Take(page.PageSize);

    private static IQueryable<TEntity> ApplySort<TEntity>(IQueryable<TEntity> source, SortExpression<TEntity> ordering, bool thenBy)
    {
        var method = thenBy
            ? (ordering.Descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy))
            : (ordering.Descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy));
        var expression = Expression.Call(typeof(Queryable), method,
            new[] { typeof(TEntity), ordering.KeySelector.ReturnType }, source.Expression, Expression.Quote(ordering.KeySelector));
        return source.Provider.CreateQuery<TEntity>(expression);
    }
}
