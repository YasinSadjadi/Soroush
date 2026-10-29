using System.Linq.Expressions;

namespace Soroush.Mediator.Abstractions;

/// <summary>Model-bindable filters, ordering, and paging addressed by selected output field names.</summary>
public class QueryOptions
{
    /// <summary>Gets or sets the filters. An empty collection matches every selected row.</summary>
    public IReadOnlyList<QueryFilter> FilterBy { get; set; } = Array.Empty<QueryFilter>();

    /// <summary>Gets or sets primary and subsequent sort keys in application order.</summary>
    public IReadOnlyList<QuerySort> OrderBy { get; set; } = Array.Empty<QuerySort>();

    /// <summary>Gets or sets how filters are combined. Defaults to AND.</summary>
    public FilterLogic Logic { get; set; }

    /// <summary>Gets or sets the one-based page number. Defaults to one.</summary>
    public int PageNumber { get; set; } = 1;

    /// <summary>Gets or sets the positive page size. Defaults to twenty.</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>Builds a validated snapshot using the output type's public fields and readable properties.</summary>
    /// <remarks>A projected Id is used as the default order and appended as a tie breaker when available.
    /// Otherwise supply an ordering containing a unique output field. This overload permits all public output
    /// members; use explicit field maps or a configured projected query handler for restricted client requests.</remarks>
    public AdvancedQuery<TResult> ToQuery<TResult>()
    {
        var fields = OutputFields<TResult>.Map.Value;
        return ToQuery(fields, fields);
    }

    /// <summary>Builds a validated snapshot using separate explicit permissions for filtering and ordering.</summary>
    /// <remarks>Every requested name must be permitted for that operation. A projected Id remains the server's
    /// fixed default order and tie breaker; that does not grant clients permission to filter or order by Id.</remarks>
    public AdvancedQuery<TResult> ToQuery<TResult>(QueryFieldMap<TResult> filterableFields, QueryFieldMap<TResult> orderableFields)
    {
        if (filterableFields is null) throw new ArgumentNullException(nameof(filterableFields));
        if (orderableFields is null) throw new ArgumentNullException(nameof(orderableFields));
        var page = new PageRequest(PageNumber, PageSize);
        if (FilterBy is null) throw new ArgumentException("Filters cannot be null.", nameof(FilterBy));
        if (OrderBy is null) throw new ArgumentException("Sort keys cannot be null.", nameof(OrderBy));
        if (FilterBy.Any(filter => filter is null)) throw new ArgumentException("Filters cannot contain null.", nameof(FilterBy));
        if (OrderBy.Any(sort => sort is null)) throw new ArgumentException("Sort keys cannot contain null.", nameof(OrderBy));
        if (Logic != FilterLogic.And && Logic != FilterLogic.Or) throw new ArgumentOutOfRangeException(nameof(Logic));

        var query = AdvancedQuery<TResult>.All;
        var predicates = FilterBy.Select(filter => filterableFields.FilterBy(filter.Field, filter.Operator, filter.GetValue())).ToArray();
        if (Logic == FilterLogic.Or && predicates.Length > 0) query = query.FilterByAny(predicates);
        else foreach (var predicate in predicates) query = query.FilterBy(predicate);

        foreach (var sort in OrderBy)
        {
            var ordering = orderableFields.OrderBy(sort.Field, sort.Descending);
            query = query.Orderings.Count == 0 ? query.OrderBy(ordering) : query.ThenBy(ordering);
        }
        var outputFields = OutputFields<TResult>.Map.Value;
        if (outputFields.HasField("Id"))
        {
            var identity = outputFields.OrderBy("Id");
            if (!query.Orderings.Any(ordering => SelectsSameMember(ordering.KeySelector, identity.KeySelector)))
                query = query.Orderings.Count == 0 ? query.OrderBy(identity) : query.ThenBy(identity);
        }
        if (query.Orderings.Count == 0)
            throw new ArgumentException("Specify OrderBy when the selected output has no public Id field.", nameof(OrderBy));

        return query.PageBy(page);
    }

    private static bool SelectsSameMember(LambdaExpression left, LambdaExpression right)
        => left.Body is MemberExpression leftMember && right.Body is MemberExpression rightMember
            && leftMember.Expression == left.Parameters[0] && rightMember.Expression == right.Parameters[0]
            && leftMember.Member.Equals(rightMember.Member);

    private static class OutputFields<TResult>
    {
        internal static readonly Lazy<QueryFieldMap<TResult>> Map = new Lazy<QueryFieldMap<TResult>>(QueryFieldMap<TResult>.FromPublicMembers);
    }
}
