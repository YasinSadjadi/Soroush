using Soroush.Domain.Abstractions;

namespace Soroush.Persistence.Abstractions;

/// <summary>Reads entities and stages changes in the associated unit of work.</summary>
/// <remarks>These operations do not commit. Persist changes through <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// Adapters decide whether removal is physical or logical.</remarks>
public interface IRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    /// <summary>Stages a new entity, allowing asynchronous key allocation when necessary.</summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Stages an existing entity for update.</summary>
    void Update(TEntity entity);

    /// <summary>Stages an existing entity for removal.</summary>
    void Remove(TEntity entity);
}
