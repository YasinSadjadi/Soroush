using System.Linq.Expressions;

namespace Soroush.Persistence.Abstractions;

/// <summary>A predicate and ordered sort keys that an adapter translates into a query.</summary>
/// <remarks>Adapters must reject unsupported expressions explicitly rather than silently evaluate on the client.</remarks>
public interface ISpecification<TEntity>
{
    /// <summary>Gets the filter. Use a predicate returning true to match all entities.</summary>
    Expression<Func<TEntity, bool>> Criteria { get; }

    /// <summary>Gets primary and subsequent sort keys in application order.</summary>
    IReadOnlyList<SortExpression<TEntity>> Orderings { get; }
}
