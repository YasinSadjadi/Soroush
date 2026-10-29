using Soroush.Domain.Abstractions;

namespace Soroush.Persistence.Abstractions;

/// <summary>Reads entities without exposing a provider-specific query API.</summary>
/// <remarks>Tracking, relationship loading, and soft deletion filtering are adapter policies.
/// Count and existence checks apply only the specification predicate; ordering applies to lists, pages, and streams.</remarks>
public interface IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    /// <summary>Finds an entity by identity, or returns null if none exists.</summary>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>Lists all matching entities, ordered according to the specification.</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Returns a page and the total matching count before paging.
    /// Adapters must apply a stable order, using identity as a tie breaker.</summary>
    Task<PagedResult<TEntity>> PageAsync(ISpecification<TEntity> specification, PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Counts matching entities before paging.</summary>
    Task<long> CountAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Determines whether any entity matches.</summary>
    Task<bool> AnyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Streams matching entities. Enumeration must remain within the adapter's lifetime.</summary>
    IAsyncEnumerable<TEntity> StreamAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);
}
