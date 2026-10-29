using Soroush.Domain.Abstractions;
using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

// A read-only view borrows the canonical write repository. Only its canonical service owns disposal.
internal sealed class ReadRepositoryView<TEntity, TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
    where TKey : notnull
{
    private readonly IRepository<TEntity, TKey> _repository;

    public ReadRepositoryView(IRepository<TEntity, TKey> repository) => _repository = repository;

    public Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        => _repository.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<TEntity>> ListAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        => _repository.ListAsync(specification, cancellationToken);

    public Task<PagedResult<TEntity>> PageAsync(ISpecification<TEntity> specification, PageRequest page, CancellationToken cancellationToken = default)
        => _repository.PageAsync(specification, page, cancellationToken);

    public Task<long> CountAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        => _repository.CountAsync(specification, cancellationToken);

    public Task<bool> AnyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        => _repository.AnyAsync(specification, cancellationToken);

    public IAsyncEnumerable<TEntity> StreamAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        => _repository.StreamAsync(specification, cancellationToken);
}
