using Soroush.Domain.Abstractions;
using Soroush.Persistence.Abstractions;

namespace Soroush.Persistence;

/// <summary>Connects advanced queries to existing repository contracts.</summary>
public static class ReadRepositoryQueryExtensions
{
    /// <summary>Executes a query using its page request. Call PageBy before executing.</summary>
    /// <remarks>The repository adapter remains responsible for a stable order and counting before paging.</remarks>
    public static Task<PagedResult<TEntity>> PageAsync<TEntity, TKey>(this IReadRepository<TEntity, TKey> repository,
        AdvancedQuery<TEntity> query, CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (repository is null) throw new ArgumentNullException(nameof(repository));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Page is null) throw new InvalidOperationException("Call PageBy before requesting a repository page.");
        return repository.PageAsync(query, query.Page, cancellationToken);
    }
}
