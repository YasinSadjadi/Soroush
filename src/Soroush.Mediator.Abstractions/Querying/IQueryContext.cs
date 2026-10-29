namespace Soroush.Mediator.Abstractions;

/// <summary>Adapts an application's query source and native execution methods once for all projected queries.</summary>
/// <remarks>Keep queries on the original provider and honor cancellation. The adapter owns tracking,
/// authorization scope, tenant restrictions, and soft deletion policies; it must not evaluate unsupported
/// expressions on the client. Count and list operations run sequentially on the same scoped adapter.</remarks>
public interface IQueryContext
{
    /// <summary>Returns the scoped entity source. Apply mandatory source restrictions here.</summary>
    IQueryable<TEntity> Query<TEntity>() where TEntity : class;

    /// <summary>Counts the selected and filtered rows before ordering and paging.</summary>
    Task<long> CountAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default);

    /// <summary>Materializes the selected, filtered, ordered, and paged query using the provider's native API.</summary>
    Task<IReadOnlyList<TResult>> ListAsync<TResult>(IQueryable<TResult> query, CancellationToken cancellationToken = default);
}
