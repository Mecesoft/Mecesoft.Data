using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

#pragma warning disable CA1068 // Legacy Mecesoft.Repository signatures place CancellationToken before optional flags.

namespace Mecesoft.Repository;

/// <summary>
/// Provides the Mecesoft.Repository v1 repository implementation for any DbContext.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TContext">The DbContext type, including standard, Identity, and custom contexts.</typeparam>
/// <remarks>
/// <para>
/// This class preserves the legacy method signatures and adds explicit helpers for detached
/// optimistic-concurrency updates. It does not replace the secure repositories in
/// <c>Mecesoft.Data.EntityFrameworkCore</c> and does not bypass mandatory query filters. With a
/// <c>MecesoftDbContext</c>, normal tenant read grants, write grants, and SaveChanges validation
/// remain active.
/// </para>
/// <para>
/// <c>includeDeleted</c> removes only the named <c>Mecesoft.SoftDelete</c> filter. It is rejected for
/// non-soft-deletable entities and for <c>MecesoftDbContext</c>; the legacy behavior of removing all
/// filters is intentionally not reproduced.
/// </para>
/// <para>
/// Delete methods are physical deletes. Use <see cref="ISoftDeleteService{TEntity,TKey}"/> for
/// soft-delete and restore in non-tenant applications.
/// </para>
/// </remarks>
public class Repository<TEntity, TContext> : IRepository<TEntity>
    where TEntity : class
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly DbSet<TEntity> _entity;

    /// <summary>
    /// Initializes a new legacy-compatible repository.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    public Repository(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _entity = context.Set<TEntity>();
    }

    /// <summary>
    /// Gets the DbContext that owns this repository.
    /// </summary>
    protected TContext Context => _context;

    /// <inheritdoc />
    public IQueryable<TEntity> GetAll(bool includeDeleted = false, bool asNoTracking = true)
    {
        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        return LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
    }

    /// <inheritdoc />
    public IQueryable<TEntity> Where(
        Expression<Func<TEntity, bool>> expression,
        bool includeDeleted = false,
        bool asNoTracking = true)
    {
        ArgumentNullException.ThrowIfNull(expression);

        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return query.Where(expression);
    }

    /// <inheritdoc />
    public async Task<TEntity?> GetByExpressionAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true)
    {
        ArgumentNullException.ThrowIfNull(expression);

        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return await query.Where(expression)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TResult?> GetByExpressionDtoAsync<TResult>(
        Expression<Func<TEntity, bool>> predicate,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(selector);

        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return await query.Where(predicate)
            .Select(selector)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TEntity?> GetFirstAsync(
        CancellationToken cancellationToken = default,
        bool includeDeleted = false,
        bool asNoTracking = true)
    {
        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return _entity.AnyAsync(expression, cancellationToken);
    }

    /// <inheritdoc />
    public bool Any(Expression<Func<TEntity, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return _entity.Any(expression);
    }

    /// <inheritdoc />
    public TEntity? GetByExpression(
        Expression<Func<TEntity, bool>> expression,
        bool includeDeleted = false,
        bool asNoTracking = true)
    {
        ArgumentNullException.ThrowIfNull(expression);

        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return query.Where(expression).FirstOrDefault();
    }

    /// <inheritdoc />
    public TEntity? GetFirst(bool includeDeleted = false, bool asNoTracking = true)
    {
        IQueryable<TEntity> query = asNoTracking ? _entity.AsNoTracking() : _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return query.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _entity.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Add(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entity.Add(entity);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(
        ICollection<TEntity> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        await _entity.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Update(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entity.Update(entity);
    }

    /// <summary>
    /// Marks a detached entity as modified and pins the original concurrency token.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="originalConcurrencyToken">
    /// The concurrency token value that was read before the entity was detached. For a SQL Server
    /// <c>byte[] RowVersion</c> this is the original byte array.
    /// </param>
    /// <remarks>
    /// The entity must have a property configured with <c>IsConcurrencyToken()</c> (for example
    /// <c>[Timestamp]</c> or <c>IsRowVersion()</c>). For store-generated tokens the current value is
    /// not written back; for application-managed tokens the caller must put the new current value on
    /// the entity. A mismatch causes EF Core to throw <c>DbUpdateConcurrencyException</c> on save.
    /// </remarks>
    public void Update(TEntity entity, object originalConcurrencyToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(originalConcurrencyToken);

        EntityEntry<TEntity> entry = _entity.Update(entity);
        IProperty? tokenProperty = entry.Metadata.GetProperties()
            .SingleOrDefault(property => property.IsConcurrencyToken);

        if (tokenProperty is null)
        {
            throw new InvalidOperationException(
                $"The entity type '{typeof(TEntity).Name}' does not define a concurrency token. "
                + "Configure one with IsConcurrencyToken(), IsRowVersion(), or [Timestamp].");
        }

        entry.Property(tokenProperty.Name).OriginalValue = originalConcurrencyToken;

        if (tokenProperty.ValueGenerated == ValueGenerated.OnAddOrUpdate)
        {
            entry.Property(tokenProperty.Name).IsModified = false;
        }
    }

    /// <inheritdoc />
    public void UpdateRange(ICollection<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entity.UpdateRange(entities);
    }

    /// <inheritdoc />
    public async Task DeleteByIdAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        TEntity? entity = await _entity.FindAsync(id).ConfigureAwait(false);
        if (entity is not null)
        {
            _entity.Remove(entity);
        }
    }

    /// <inheritdoc />
    public async Task DeleteByExpressionAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);

        TEntity? entity = await _entity
            .Where(expression)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entity is not null)
        {
            _entity.Remove(entity);
        }
    }

    /// <inheritdoc />
    public void Delete(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _entity.Remove(entity);
    }

    /// <inheritdoc />
    public void DeleteRange(ICollection<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entity.RemoveRange(entities);
    }

    /// <summary>
    /// Returns the first tracked entity matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <returns>The tracked entity or <see langword="null"/>.</returns>
    public TEntity? GetByExpressionWithTracking(Expression<Func<TEntity, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return _entity.Where(expression).FirstOrDefault();
    }

    /// <summary>
    /// Returns the first tracked entity matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracked entity or <see langword="null"/>.</returns>
    public async Task<TEntity?> GetByExpressionWithTrackingAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return await _entity.Where(expression)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the first tracked entity matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <returns>The tracked entity or <see langword="null"/>.</returns>
    public async Task<TEntity?> GetByExpressionWithTrackingAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(expression);

        IQueryable<TEntity> query = _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return await query.Where(expression)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns all tracked entities matching a predicate.
    /// </summary>
    /// <param name="expression">The filter predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="includeDeleted">Whether to remove only the named soft-delete filter.</param>
    /// <returns>The tracked entities.</returns>
    public async Task<List<TEntity>> GetAllExpressionWithTrackingAsync(
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(expression);

        IQueryable<TEntity> query = _entity.AsTracking();
        query = LegacySoftDeleteQuery.Apply(query, _context, includeDeleted);
        return await query.Where(expression)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

#pragma warning restore CA1068
