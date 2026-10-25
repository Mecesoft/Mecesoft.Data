using System.Linq.Expressions;

#pragma warning disable CA1068 // Legacy Mecesoft.Repository signatures place CancellationToken before optional flags.


namespace Mecesoft.Repository;

/// <summary>
/// Provides the Mecesoft.Repository v1 repository surface for source compatibility.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <remarks>
/// <para>
/// This interface preserves the public signatures of the legacy <c>Mecesoft.Repository</c> package
/// so existing applications can move to Mecesoft.Data with minimal source changes. It intentionally
/// exposes <see cref="IQueryable{T}"/> because legacy callers compose LINQ, <c>Include</c>, and
/// <c>ThenInclude</c> queries directly.
/// </para>
/// <para>
/// The compatibility layer is not a security boundary. It does not apply the resolver-issued grants
/// used by the secure <c>IRepository&lt;TEntity, TKey&gt;</c> API. With a
/// <c>MecesoftDbContext</c>, the mandatory tenant and soft-delete filters remain active and a
/// resolver-issued read scope is still required. <c>includeDeleted</c> is rejected for
/// <c>MecesoftDbContext</c> and for entity types that do not have a named Mecesoft soft-delete filter.
/// </para>
/// <para>
/// Prefer the secure repository contracts for new code. See the migration guide for the
/// security-sensitive differences.
/// </para>
/// </remarks>
public interface IRepository<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Gets all entities permitted by the active query filters.
    /// </summary>
    /// <param name="includeDeleted">
    /// <see langword="true"/> to remove only the named soft-delete filter. This is supported only for
    /// <c>ISoftDeletable</c> entities on a non-Mecesoft context that has the Mecesoft named
    /// soft-delete filter installed.
    /// </param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>A composable query.</returns>
    IQueryable<TEntity> GetAll(bool includeDeleted = false, bool asNoTracking = true);

    /// <summary>
    /// Filters entities permitted by the active query filters.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>A composable query.</returns>
    IQueryable<TEntity> Where(
        Expression<Func<TEntity, bool>> expression,
        bool includeDeleted = false,
        bool asNoTracking = true);

    /// <summary>
    /// Returns the first entity matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>The entity or <see langword="null"/>.</returns>
    Task<TEntity?> GetByExpressionAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true);

    /// <summary>
    /// Returns the first projected value matching a predicate.
    /// </summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="predicate">The filter predicate.</param>
    /// <param name="selector">The projection selector.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>The projected value or the default value.</returns>
    Task<TResult?> GetByExpressionDtoAsync<TResult>(
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true);

    /// <summary>
    /// Returns the first entity.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>The entity or <see langword="null"/>.</returns>
    Task<TEntity?> GetFirstAsync(
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true);

    /// <summary>
    /// Determines asynchronously whether any entity matches a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when a match exists.</returns>
    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines synchronously whether any entity matches a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <returns><see langword="true"/> when a match exists.</returns>
    bool Any(Expression<Func<TEntity, bool>> expression);

    /// <summary>
    /// Returns the first entity matching a predicate synchronously.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>The entity or <see langword="null"/>.</returns>
    TEntity? GetByExpression(
        Expression<Func<TEntity, bool>> expression,
        bool includeDeleted = false,
        bool asNoTracking = true);

    /// <summary>
    /// Returns the first entity synchronously.
    /// </summary>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <param name="asNoTracking"><see langword="true"/> to disable change tracking.</param>
    /// <returns>The entity or <see langword="null"/>.</returns>
    TEntity? GetFirst(bool includeDeleted = false, bool asNoTracking = true);

    /// <summary>
    /// Adds an entity.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the entity is tracked.</returns>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an entity synchronously.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    void Add(TEntity entity);

    /// <summary>
    /// Adds a collection of entities.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the entities are tracked.</returns>
    Task AddRangeAsync(ICollection<TEntity> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an entity as modified.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    void Update(TEntity entity);

    /// <summary>
    /// Marks a collection of entities as modified.
    /// </summary>
    /// <param name="entities">The entities to update.</param>
    void UpdateRange(ICollection<TEntity> entities);

    /// <summary>
    /// Deletes an entity by its string key.
    /// </summary>
    /// <param name="id">The string key value.</param>
    /// <returns>A task that completes when the entity is tracked for deletion.</returns>
    Task DeleteByIdAsync(string id);

    /// <summary>
    /// Deletes the first entity matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the entity is tracked for deletion.</returns>
    Task DeleteByExpressionAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an entity for deletion.
    /// </summary>
    /// <param name="entity">The entity to delete.</param>
    void Delete(TEntity entity);

    /// <summary>
    /// Marks a collection of entities for deletion.
    /// </summary>
    /// <param name="entities">The entities to delete.</param>
    void DeleteRange(ICollection<TEntity> entities);
}

#pragma warning restore CA1068
