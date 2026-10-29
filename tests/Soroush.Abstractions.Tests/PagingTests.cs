using Soroush.Persistence.Abstractions;

namespace Soroush.Abstractions.Tests;

public class PagingTests
{
    [Fact]
    public void RequestsUseOneBasedPagesAndCheckedOffsets()
    {
        var defaults = new PageRequest();
        Assert.Equal(1, defaults.PageNumber);
        Assert.Equal(20, defaults.PageSize);
        Assert.Equal(0, defaults.Offset);
        Assert.Equal(40, new PageRequest(3, 20).Offset);
        Assert.Equal(int.MaxValue - 1, new PageRequest(int.MaxValue, 1).Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(int.MaxValue, 2));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void InvalidRequestsAreRejected(int pageNumber, int pageSize)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(pageNumber, pageSize));

    [Theory]
    [InlineData(0, 1, 20, 0, false, false)]
    [InlineData(21, 1, 20, 2, false, true)]
    [InlineData(21, 2, 20, 2, true, false)]
    [InlineData(20, 2, 20, 1, true, false)]
    [InlineData(0, 2, 20, 0, false, false)]
    [InlineData(long.MaxValue, 1, 2, 4611686018427387904L, false, true)]
    public void PageMetadataHandlesEmptyOutOfRangeAndLargeCounts(long total, int number, int size, long pages, bool previous, bool next)
    {
        var result = new PagedResult<int>(Array.Empty<int>(), total, new PageRequest(number, size));
        Assert.Equal(pages, result.TotalPages);
        Assert.Equal(previous, result.HasPreviousPage);
        Assert.Equal(next, result.HasNextPage);
    }

    [Fact]
    public void PageItemsAreAnImmutableSnapshot()
    {
        var source = new List<int> { 1, 2 };
        var result = new PagedResult<int>(source, 3, new PageRequest(1, 2));
        source[0] = 99;
        source.Add(3);

        Assert.Equal(new[] { 1, 2 }, result.Items);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)result.Items).Add(4));
    }

    [Fact]
    public void InconsistentResultsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new PagedResult<int>(null!, 0, new PageRequest()));
        Assert.Throws<ArgumentNullException>(() => new PagedResult<int>(Array.Empty<int>(), 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PagedResult<int>(Array.Empty<int>(), -1, new PageRequest()));
        Assert.Throws<ArgumentException>(() => new PagedResult<int>(new[] { 1, 2 }, 2, new PageRequest(1, 1)));
        Assert.Throws<ArgumentException>(() => new PagedResult<int>(new[] { 1 }, 0, new PageRequest()));
    }
}
