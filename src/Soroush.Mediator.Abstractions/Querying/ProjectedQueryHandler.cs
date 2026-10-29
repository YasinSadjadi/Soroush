using System.Linq.Expressions;
using Soroush.Persistence;
using Soroush.Persistence.Abstractions;

namespace Soroush.Mediator.Abstractions;

/// <summary>Base handler applying an entity projection, named output filters, ordering, and paging.</summary>
/// <remarks>Supply Mapping and explicitly register filterable and orderable DTO members.
/// Client filtering and ordering are denied by default. The query request carries only input options.</remarks>
public abstract class ProjectedQueryHandler<TQuery, TEntity, TResult> : IQueryHandler<TQuery, PagedResult<TResult>>
    where TQuery : ProjectedQuery<TResult>
    where TEntity : class
{
    private readonly IQueryContext _context;

    /// <summary>Creates a handler using the shared scoped query provider adapter.</summary>
    protected ProjectedQueryHandler(IQueryContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Gets the provider-translatable entity-to-output mapping used by Select.</summary>
    protected abstract Expression<Func<TEntity, TResult>> Mapping { get; }

    /// <summary>Registers DTO members clients may filter. Defaults to allowing none.</summary>
    protected virtual void ConfigureFilterableFields(QueryFieldRegistry<TResult> fields) { }

    /// <summary>Registers DTO members clients may order by. Defaults to allowing none.</summary>
    protected virtual void ConfigureOrderableFields(QueryFieldRegistry<TResult> fields) { }

    /// <summary>Creates the entity source. Override to apply request-specific restrictions before Select.</summary>
    protected virtual IQueryable<TEntity> CreateQuery(TQuery request) => _context.Query<TEntity>();

    /// <inheritdoc />
    public async Task<PagedResult<TResult>> Handle(TQuery request, CancellationToken cancellationToken)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var specification = BuildSpecification(request);
        var selected = Select(CreateQuery(request));
        var count = await _context.CountAsync(selected.ApplyFilters(specification), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var items = await _context.ListAsync(selected.ApplyQuery(specification), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new PagedResult<TResult>(items, count, specification.Page!);
    }

    /// <summary>Builds the same selected, filtered, ordered, and paged query over a supplied source without executing it.</summary>
    /// <remarks>The supplied source replaces CreateQuery; existing source restrictions are preserved.</remarks>
    public IQueryable<TResult> ApplyTo(IQueryable<TEntity> source, TQuery request)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (request is null) throw new ArgumentNullException(nameof(request));
        var specification = BuildSpecification(request);
        return Select(source).ApplyQuery(specification);
    }

    private AdvancedQuery<TResult> BuildSpecification(TQuery request)
    {
        // Registries belong to this handler invocation, never to the shared DTO type.
        var filterable = new QueryFieldRegistry<TResult>();
        var orderable = new QueryFieldRegistry<TResult>();
        ConfigureFilterableFields(filterable);
        ConfigureOrderableFields(orderable);
        return request.ToQuery(filterable.Build(), orderable.Build());
    }

    private IQueryable<TResult> Select(IQueryable<TEntity> source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var mapping = Mapping ?? throw new InvalidOperationException("The handler mapping cannot be null.");
        return source.Select(mapping);
    }
}
