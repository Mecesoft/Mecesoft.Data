namespace Mecesoft.Data.Abstractions.Pagination;

/// <summary>
/// Represents a one-based offset page request.
/// </summary>
public sealed record PageRequest
{
    /// <summary>
    /// Initializes a new page request.
    /// </summary>
    /// <param name="pageNumber">The one-based page number.</param>
    /// <param name="pageSize">The positive number of items requested per page.</param>
    public PageRequest(int pageNumber, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    /// <summary>
    /// Gets the one-based page number.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// Gets the number of items requested per page.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the zero-based row offset using 64-bit arithmetic.
    /// </summary>
    public long Offset => checked((long)(PageNumber - 1) * PageSize);
}
