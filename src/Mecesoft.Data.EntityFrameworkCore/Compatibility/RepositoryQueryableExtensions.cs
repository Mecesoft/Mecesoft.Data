using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Repository;

/// <summary>
/// Pagination helpers for legacy-style <see cref="IQueryable{T}"/> repository queries.
/// </summary>
/// <remarks>
/// <see cref="ToPageAsync{TEntity}(IQueryable{TEntity}, PageRequest, CancellationToken)"/> requires
/// the caller to order the query. Use
/// <see cref="ToStablePageAsync{TEntity,TKey}"/> to order by a unique key first.
/// </remarks>
public static class RepositoryQueryableExtensions
{
    /// <summary>
    /// Materializes a page and its total count from an ordered query.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The ordered query.</param>
    /// <param name="request">The requested page.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The materialized page.</returns>
    public static async Task<PageResult<TEntity>> ToPageAsync<TEntity>(
        this IQueryable<TEntity> query,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(request);

        int offset = checked((int)request.Offset);
        long totalCount = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        List<TEntity> items = await query
            .Skip(offset)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PageResult<TEntity>(
            items,
            request.PageNumber,
            request.PageSize,
            totalCount);
    }

    /// <summary>
    /// Materializes a page and its total count from an ordered query.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The ordered query.</param>
    /// <param name="pageNumber">The one-based page number.</param>
    /// <param name="pageSize">The page size.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The materialized page.</returns>
    public static Task<PageResult<TEntity>> ToPageAsync<TEntity>(
        this IQueryable<TEntity> query,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return query.ToPageAsync(
            new PageRequest(pageNumber, pageSize),
            cancellationToken);
    }

    /// <summary>
    /// Orders a query by a unique key and materializes a page.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The ordering key type.</typeparam>
    /// <param name="query">The source query.</param>
    /// <param name="request">The requested page.</param>
    /// <param name="keySelector">A unique ordering key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The materialized page.</returns>
    /// <remarks>
    /// The key selector is applied with <see cref="Queryable.OrderBy{TSource,TKey}(IQueryable{TSource}, Expression{Func{TSource,TKey}})"/>.
    /// Apply any secondary ordering before calling this method only when the key alone is not unique.
    /// </remarks>
    public static Task<PageResult<TEntity>> ToStablePageAsync<TEntity, TKey>(
        this IQueryable<TEntity> query,
        PageRequest request,
        Expression<Func<TEntity, TKey>> keySelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(keySelector);

        return query.OrderBy(keySelector).ToPageAsync(request, cancellationToken);
    }
}
