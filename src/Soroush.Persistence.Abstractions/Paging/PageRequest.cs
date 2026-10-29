namespace Soroush.Persistence.Abstractions;

/// <summary>A validated, one-based page request.</summary>
public sealed class PageRequest
{
    /// <summary>Creates a page request. Page numbers and sizes must be positive and the offset must fit an Int32.</summary>
    public PageRequest(int pageNumber = 1, int pageSize = 20)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var offset = ((long)pageNumber - 1) * pageSize;
        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "The page offset exceeds Int32.MaxValue.");
        PageNumber = pageNumber;
        PageSize = pageSize;
        Offset = (int)offset;
    }

    /// <summary>Gets the one-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>Gets the maximum number of items requested.</summary>
    public int PageSize { get; }

    /// <summary>Gets the number of items to skip.</summary>
    public int Offset { get; }
}
