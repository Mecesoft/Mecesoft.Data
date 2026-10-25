namespace Mecesoft.Data.Abstractions.Pagination;

/// <summary>
/// Represents a materialized page and its total-count metadata.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class PageResult<T>
{
    /// <summary>
    /// Initializes a new page result.
    /// </summary>
    /// <param name="items">The materialized page items.</param>
    /// <param name="pageNumber">The one-based page number.</param>
    /// <param name="pageSize">The positive page size.</param>
    /// <param name="totalCount">The non-negative number of matching items.</param>
    public PageResult(IEnumerable<T> items, int pageNumber, int pageSize, long totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        T[] materializedItems = items.ToArray();
        if (materializedItems.LongLength > totalCount)
        {
            throw new ArgumentException("The page cannot contain more items than the total count.", nameof(items));
        }

        Items = Array.AsReadOnly(materializedItems);
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    /// <summary>
    /// Gets the materialized page items.
    /// </summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>
    /// Gets the one-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Gets the page size.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the total number of matching items.
    /// </summary>
    public long TotalCount { get; }

    /// <summary>
    /// Gets the total number of pages.
    /// </summary>
    public long TotalPages => TotalCount == 0 ? 0 : ((TotalCount - 1) / PageSize) + 1;

    /// <summary>
    /// Gets a value indicating whether a previous page exists.
    /// </summary>
    public bool HasPreviousPage => PageNumber > 1 && TotalCount > 0;

    /// <summary>
    /// Gets a value indicating whether a following page exists.
    /// </summary>
    public bool HasNextPage => PageNumber < TotalPages;
}
