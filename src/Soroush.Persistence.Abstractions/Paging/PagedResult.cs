namespace Soroush.Persistence.Abstractions;

/// <summary>An immutable page snapshot with the total count before paging.</summary>
/// <remarks>Items and count should come from the same snapshot when the adapter supports it.
/// Concurrent writes may otherwise cause them to differ.</remarks>
public sealed class PagedResult<T>
{
    /// <summary>Creates a result, copying the items and validating the page size and total count.</summary>
    public PagedResult(IEnumerable<T> items, long totalCount, PageRequest page)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (totalCount < 0) throw new ArgumentOutOfRangeException(nameof(totalCount));
        Page = page ?? throw new ArgumentNullException(nameof(page));
        var snapshot = items.ToArray();
        if (snapshot.Length > page.PageSize)
            throw new ArgumentException("The item count exceeds the requested page size.", nameof(items));
        if (snapshot.LongLength > totalCount)
            throw new ArgumentException("The item count exceeds the total count.", nameof(items));
        Items = Array.AsReadOnly(snapshot);
        TotalCount = totalCount;
    }

    /// <summary>Gets the items in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Gets the total number of matching items before paging.</summary>
    public long TotalCount { get; }

    /// <summary>Gets the page request used to produce this result.</summary>
    public PageRequest Page { get; }

    /// <summary>Gets the total page count; an empty result has zero pages.</summary>
    public long TotalPages => TotalCount / Page.PageSize + (TotalCount % Page.PageSize == 0 ? 0 : 1);

    /// <summary>Gets whether an earlier page containing results exists.</summary>
    public bool HasPreviousPage => Page.PageNumber > 1 && TotalCount > 0;

    /// <summary>Gets whether another page exists after this page.</summary>
    public bool HasNextPage => Page.PageNumber < TotalPages;
}
