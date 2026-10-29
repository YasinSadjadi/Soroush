
namespace Soroush.Mediator.Abstractions;

/// <summary>Base request carrying named output filters, ordering, and paging for a DTO response.</summary>
/// <remarks>Define the entity source and output mapping in a concrete ProjectedQueryHandler.</remarks>
public abstract class ProjectedQuery<TResult> : QueryOptions, IQuery<PagedResult<TResult>> { }
