using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Expressions;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Implements optional soft-delete and restore for a standard DbContext.
/// </summary>
/// <typeparam name="TContext">The DbContext type.</typeparam>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <remarks>
/// <para>
/// The service removes only the named <c>Mecesoft.SoftDelete</c> filter. Any tenant or application
/// filter remains active. It refuses to operate on a <c>MecesoftDbContext</c>; use the secure
/// soft-delete repository there.
/// </para>
/// <para>
/// The service performs no authorization and does not implement optimistic concurrency beyond the
/// entity's configured EF Core concurrency token. Callers must authorize the operation.
/// </para>
/// </remarks>
public sealed class SoftDeleteService<TContext, TEntity, TKey>
    : ISoftDeleteService<TEntity, TKey>
    where TContext : DbContext
    where TEntity : class, ISoftDeletable
    where TKey : notnull
{
    private readonly TContext _context;
    private readonly Expression<Func<TEntity, TKey>> _keySelector;
    private readonly IProperty _isDeletedProperty;
    private readonly IProperty? _deletedAtProperty;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new soft-delete service.
    /// </summary>
    /// <param name="context">The scoped standard DbContext.</param>
    /// <param name="keySelector">The single-column primary-key selector.</param>
    /// <param name="timeProvider">The clock used for <c>DeletedAtUtc</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// The context is a <c>MecesoftDbContext</c>, the named soft-delete filter is not configured, or
    /// the entity does not map the required properties.
    /// </exception>
    public SoftDeleteService(
        TContext context,
        Expression<Func<TEntity, TKey>> keySelector,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keySelector);
        KeySelectorValidator.Validate(context, keySelector);

        if (context is Mecesoft.Data.EntityFrameworkCore.MecesoftDbContext)
        {
            throw new InvalidOperationException(
                "SoftDeleteService cannot be used with MecesoftDbContext. Use the authorized "
                + "soft-delete/restore repository.");
        }

        IEntityType entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException(
                $"The entity type '{typeof(TEntity).Name}' is not part of the DbContext model.");

        if (!HasNamedSoftDeleteFilter(entityType))
        {
            throw new InvalidOperationException(
                "The entity does not have the named 'Mecesoft.SoftDelete' query filter. Call "
                + "ModelBuilder.ApplyMecesoftSoftDeleteFilters() in the standard DbContext.");
        }

        _isDeletedProperty = entityType.FindProperty(nameof(ISoftDeletable.IsDeleted))
            ?? throw new InvalidOperationException(
                $"The entity '{typeof(TEntity).Name}' does not map "
                + $"{nameof(ISoftDeletable.IsDeleted)}.");
        _deletedAtProperty = entityType.FindProperty(nameof(ISoftDeletable.DeletedAtUtc));
        _context = context;
        _keySelector = keySelector;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SoftDeleteAsync(
        TKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        TEntity? entity = await FindIncludingDeletedAsync(key, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        if (entity.IsDeleted)
        {
            return true;
        }

        EntityEntry<TEntity> entry = _context.Entry(entity);
        entry.Property(_isDeletedProperty.Name).CurrentValue = true;

        if (_deletedAtProperty is not null)
        {
            entry.Property(_deletedAtProperty.Name).CurrentValue = _timeProvider.GetUtcNow();
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RestoreAsync(
        TKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        TEntity? entity = await FindIncludingDeletedAsync(key, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        if (!entity.IsDeleted)
        {
            return true;
        }

        EntityEntry<TEntity> entry = _context.Entry(entity);
        entry.Property(_isDeletedProperty.Name).CurrentValue = false;

        if (_deletedAtProperty is not null)
        {
            entry.Property(_deletedAtProperty.Name).CurrentValue = null;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<TEntity?> FindIncludingDeletedAsync(
        TKey key,
        CancellationToken cancellationToken)
    {
        return _context.Set<TEntity>()
            .IgnoreQueryFilters([MecesoftQueryFilterKeys.SoftDelete])
            .FirstOrDefaultAsync(BuildKeyPredicate(key), cancellationToken);
    }

    private Expression<Func<TEntity, bool>> BuildKeyPredicate(TKey key)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "entity");
        var replacer = new ExpressionParameterReplacer(_keySelector.Parameters[0], parameter);
        Expression body = replacer.Visit(_keySelector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(body, Expression.Constant(key, typeof(TKey))),
            parameter);
    }

    private static bool HasNamedSoftDeleteFilter(IEntityType entityType)
    {
        IReadOnlyEntityType? current = entityType;
        while (current is not null)
        {
            if (current.FindDeclaredQueryFilter(MecesoftQueryFilterKeys.SoftDelete) is not null)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}
