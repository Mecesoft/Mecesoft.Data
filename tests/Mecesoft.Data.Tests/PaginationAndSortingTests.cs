using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Specifications;

namespace Mecesoft.Data.Tests;

public sealed class PaginationAndSortingTests
{
    [Fact]
    public void PageRequestValidatesInputAndCalculatesOffset()
    {
        var request = new PageRequest(3, 25);

        Assert.Equal(50, request.Offset);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(0, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(1, 0));
    }

    [Fact]
    public void PageResultMaterializesItemsAndCalculatesNavigation()
    {
        int[] source = [21, 22];
        var result = new PageResult<int>(source, 3, 10, 22);
        source[0] = -1;

        Assert.Equal([21, 22], result.Items);
        Assert.IsNotType<int[]>(result.Items);
        Assert.Equal(3, result.TotalPages);
        Assert.True(result.HasPreviousPage);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public void SortExpressionPreservesNativeKeyType()
    {
        var sort = new SortExpression<TestEntity, Guid>(entity => entity.Id, SortDirection.Descending);
        ISortExpression<TestEntity> untypedSort = sort;

        Assert.Equal(typeof(Func<TestEntity, Guid>), untypedSort.KeySelector.Type);
        Assert.Equal(SortDirection.Descending, sort.Direction);
    }

    private sealed class TestEntity
    {
        public Guid Id { get; init; }
    }
}
