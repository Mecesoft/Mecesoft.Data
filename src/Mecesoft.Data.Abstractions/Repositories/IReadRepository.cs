using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;

namespace Mecesoft.Data.Abstractions.Repositories;

/// <summary>
/// Defines read operations without exposing a provider query object.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The strongly typed entity key type.</typeparam>
public interface IReadRepository<TEntity, in TKey>
    where TEntity : class
    where TKey : notnull
{
    /// <summary>
    /// Finds an entity by key within all mandatory tenant and visibility policies.
    /// </summary>
    /// <param name="key">The entity key.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The entity, or a typed failure such as not-found or tenant-required.</returns>
    ValueTask<DataResult<TEntity>> GetByIdAsync(
        TKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds and projects an entity by key within all mandatory policies.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="key">The entity key.</param>
    /// <param name="projection">The projection to execute at the data source.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The projected value or a typed failure.</returns>
    ValueTask<DataResult<TResult>> GetByIdAsync<TResult>(
        TKey key,
        IProjection<TEntity, TResult> projection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first entity matching a specification.
    /// </summary>
    /// <param name="specification">The filter and deterministic ordering.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The first matching entity or a typed failure.</returns>
    ValueTask<DataResult<TEntity>> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first projected value matching a specification.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="specification">The projection specification.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The first projected value or a typed failure.</returns>
    ValueTask<DataResult<TResult>> FirstOrDefaultAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes entities matching a specification.
    /// </summary>
    /// <param name="specification">The filter and ordering.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A materialized read-only list or a typed failure.</returns>
    ValueTask<DataResult<IReadOnlyList<TEntity>>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes projected values matching a specification.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="specification">The projection specification.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A materialized read-only list or a typed failure.</returns>
    ValueTask<DataResult<IReadOnlyList<TResult>>> ListAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes a deterministic page of projected values.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="specification">The projection specification, including stable ordering.</param>
    /// <param name="pageRequest">The requested page.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The page or a typed failure.</returns>
    /// <remarks>
    /// The adapter must validate the ordering with
    /// <see cref="SpecificationContractGuard.ValidateForPagination{TEntity}"/> and
    /// append a unique tie-breaker, normally the primary key, before applying offset pagination.
    /// An empty or unusable order must return <see cref="UnstableOrderingFailure"/>
    /// or <see cref="UnsupportedExpressionFailure"/>; it must not be silently accepted.
    /// </remarks>
    ValueTask<DataResult<PageResult<TResult>>> PageAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        PageRequest pageRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts entities matching a specification and all mandatory policies.
    /// </summary>
    /// <param name="specification">The filter specification.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The count or a typed failure.</returns>
    ValueTask<DataResult<long>> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether any entity matches a specification and all mandatory policies.
    /// </summary>
    /// <param name="specification">The filter specification.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The existence result or a typed failure.</returns>
    ValueTask<DataResult<bool>> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default);
}
