using System.Linq.Expressions;

namespace Soroush.Persistence.Abstractions;

/// <summary>An immutable, fluent specification with optional paging.</summary>
/// <remarks>Each operation returns a new query. Repository list, count, existence, and stream
/// operations use the specification only; pass <see cref="Page"/> to a paged operation.</remarks>
public sealed class AdvancedQuery<TEntity> : ISpecification<TEntity>
{
    private readonly Specification<TEntity> _specification;

    /// <summary>Creates a query matching all entities without ordering or paging.</summary>
    public AdvancedQuery() : this(Specification<TEntity>.All, null) { }

    private AdvancedQuery(Specification<TEntity> specification, PageRequest? page)
    {
        _specification = specification;
        Page = page;
    }

    /// <summary>Gets an unfiltered query that can safely be reused.</summary>
    public static AdvancedQuery<TEntity> All { get; } = new AdvancedQuery<TEntity>();

    /// <inheritdoc />
    public Expression<Func<TEntity, bool>> Criteria => _specification.Criteria;

    /// <inheritdoc />
    public IReadOnlyList<SortExpression<TEntity>> Orderings => _specification.Orderings;

    /// <summary>Gets optional paging. Null requests all matching entities.</summary>
    public PageRequest? Page { get; }

    /// <summary>Copies an existing specification into a query without paging.</summary>
    public static AdvancedQuery<TEntity> From(ISpecification<TEntity> specification)
    {
        if (specification is null) throw new ArgumentNullException(nameof(specification));
        return new AdvancedQuery<TEntity>(
            new Specification<TEntity>(specification.Criteria, specification.Orderings.ToArray()), null);
    }

    /// <summary>Adds a predicate using AND, preserving ordering and paging.</summary>
    public AdvancedQuery<TEntity> FilterBy(Expression<Func<TEntity, bool>> predicate)
        => new AdvancedQuery<TEntity>(_specification.And(new Specification<TEntity>(predicate)), Page);

    /// <summary>Adds a predicate only when the condition is true. The predicate must always be non-null.</summary>
    public AdvancedQuery<TEntity> FilterByIf(bool condition, Expression<Func<TEntity, bool>> predicate)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return condition ? FilterBy(predicate) : this;
    }

    /// <summary>Adds an OR group using AND with existing filters. At least one predicate is required.</summary>
    public AdvancedQuery<TEntity> FilterByAny(params Expression<Func<TEntity, bool>>[] predicates)
    {
        if (predicates is null) throw new ArgumentNullException(nameof(predicates));
        if (predicates.Length == 0 || predicates.Any(predicate => predicate is null))
            throw new ArgumentException("Provide at least one non-null predicate.", nameof(predicates));

        var group = new Specification<TEntity>(predicates[0]);
        for (var index = 1; index < predicates.Length; index++)
            group = group.Or(new Specification<TEntity>(predicates[index]));
        return new AdvancedQuery<TEntity>(_specification.And(group), Page);
    }

    /// <summary>Sets the primary sort key, replacing all previous sort keys.</summary>
    public AdvancedQuery<TEntity> OrderBy<TKey>(Expression<Func<TEntity, TKey>> keySelector, bool descending = false)
        => OrderBy(SortExpression<TEntity>.Create(keySelector, descending));

    /// <summary>Sets a descending primary sort key, replacing all previous sort keys.</summary>
    public AdvancedQuery<TEntity> OrderByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector)
        => OrderBy(keySelector, descending: true);

    /// <summary>Sets a mapped primary sort key, replacing all previous sort keys.</summary>
    public AdvancedQuery<TEntity> OrderBy(SortExpression<TEntity> ordering)
    {
        if (ordering is null) throw new ArgumentNullException(nameof(ordering));
        return new AdvancedQuery<TEntity>(new Specification<TEntity>(Criteria, ordering), Page);
    }

    /// <summary>Appends a sort key. A primary sort key must already exist.</summary>
    public AdvancedQuery<TEntity> ThenBy<TKey>(Expression<Func<TEntity, TKey>> keySelector, bool descending = false)
        => ThenBy(SortExpression<TEntity>.Create(keySelector, descending));

    /// <summary>Appends a descending sort key. A primary sort key must already exist.</summary>
    public AdvancedQuery<TEntity> ThenByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector)
        => ThenBy(keySelector, descending: true);

    /// <summary>Appends a mapped sort key. A primary sort key must already exist.</summary>
    public AdvancedQuery<TEntity> ThenBy(SortExpression<TEntity> ordering)
    {
        if (ordering is null) throw new ArgumentNullException(nameof(ordering));
        if (Orderings.Count == 0)
            throw new InvalidOperationException("Call OrderBy before ThenBy.");
        return new AdvancedQuery<TEntity>(
            new Specification<TEntity>(Criteria, Orderings.Concat(new[] { ordering }).ToArray()), Page);
    }

    /// <summary>Sets validated, one-based paging.</summary>
    public AdvancedQuery<TEntity> PageBy(int pageNumber = 1, int pageSize = 20)
        => PageBy(new PageRequest(pageNumber, pageSize));

    /// <summary>Sets paging using an existing page request.</summary>
    public AdvancedQuery<TEntity> PageBy(PageRequest page)
        => new AdvancedQuery<TEntity>(_specification, page ?? throw new ArgumentNullException(nameof(page)));

    /// <summary>Removes paging while preserving filters and ordering.</summary>
    public AdvancedQuery<TEntity> WithoutPaging() => new AdvancedQuery<TEntity>(_specification, null);
}
