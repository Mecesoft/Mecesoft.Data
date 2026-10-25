using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Repositories;

/// <summary>
/// Captures protected entity state before a mutation and validates that the mutation did not change it.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <remarks>
/// <para>
/// This guard is a cooperative contract helper, not a runtime enforcement mechanism. An adapter must
/// capture the guard before invoking an <see cref="EntityMutation{TEntity}"/> and call
/// <see cref="Validate"/> after the mutation and before persistence. If validation fails, the
/// adapter must not persist and must roll back the unit of work.
/// </para>
/// <para>
/// For tenant-owned entities the adapter must supply a tenant selector. For soft-deletable entities
/// it must supply the soft-delete selectors. For <c>UpdateAsync</c> it must supply the concurrency
/// token selector. The guard protects tenant ownership, primary key, soft-delete state, and the
/// optimistic concurrency token. Related-entity ownership must be validated separately through the
/// tenant graph contract before persistence.
/// </para>
/// <para>
/// Selectors should return immutable values. A mutable reference used as a key or tenant identifier
/// can be changed in place and will not be detected by the guard.
/// </para>
/// </remarks>
public sealed class EntityMutationGuard<TEntity>
    where TEntity : class
{
    private readonly Func<TEntity, object?> _primaryKeySelector;
    private readonly Func<TEntity, object?>? _tenantIdSelector;
    private readonly Func<TEntity, bool>? _isDeletedSelector;
    private readonly Func<TEntity, DateTimeOffset?>? _deletedAtUtcSelector;
    private readonly Func<TEntity, ConcurrencyToken?>? _concurrencyTokenSelector;

    private readonly object? _primaryKey;
    private readonly object? _tenantId;
    private readonly bool _isDeleted;
    private readonly DateTimeOffset? _deletedAtUtc;
    private readonly ConcurrencyToken? _concurrencyToken;

    /// <summary>
    /// Initializes a guard by capturing the protected state of an entity.
    /// </summary>
    /// <param name="entity">The entity before mutation.</param>
    /// <param name="primaryKeySelector">The primary-key selector.</param>
    /// <param name="tenantIdSelector">The tenant-identifier selector, or <see langword="null"/> when the entity is not tenant-owned.</param>
    /// <param name="isDeletedSelector">The soft-delete flag selector, or <see langword="null"/> when the entity is not soft-deletable.</param>
    /// <param name="deletedAtUtcSelector">The deletion timestamp selector, or <see langword="null"/> when not applicable.</param>
    /// <param name="concurrencyTokenSelector">The concurrency-token selector, or <see langword="null"/> when not applicable.</param>
    public EntityMutationGuard(
        TEntity entity,
        Func<TEntity, object?> primaryKeySelector,
        Func<TEntity, object?>? tenantIdSelector = null,
        Func<TEntity, bool>? isDeletedSelector = null,
        Func<TEntity, DateTimeOffset?>? deletedAtUtcSelector = null,
        Func<TEntity, ConcurrencyToken?>? concurrencyTokenSelector = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(primaryKeySelector);

        _primaryKeySelector = primaryKeySelector;
        _tenantIdSelector = tenantIdSelector;
        _isDeletedSelector = isDeletedSelector;
        _deletedAtUtcSelector = deletedAtUtcSelector;
        _concurrencyTokenSelector = concurrencyTokenSelector;

        _primaryKey = primaryKeySelector(entity);
        _tenantId = tenantIdSelector?.Invoke(entity);
        _isDeleted = isDeletedSelector?.Invoke(entity) ?? false;
        _deletedAtUtc = deletedAtUtcSelector?.Invoke(entity);
        _concurrencyToken = concurrencyTokenSelector?.Invoke(entity);
    }

    /// <summary>
    /// Validates that the entity still has the protected state captured at construction.
    /// </summary>
    /// <param name="entity">The entity after mutation.</param>
    /// <returns>Success when every configured invariant is unchanged; otherwise, a <see cref="MutationInvariantFailure"/>.</returns>
    public DataResult<bool> Validate(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!Equals(_primaryKey, _primaryKeySelector(entity)))
        {
            return Failure("primary_key", "The primary key was modified by the entity mutation.");
        }

        if (_tenantIdSelector is not null
            && !Equals(_tenantId, _tenantIdSelector(entity)))
        {
            return Failure("tenant_id", "The tenant identifier was modified by the entity mutation.");
        }

        if (_isDeletedSelector is not null
            && _isDeleted != _isDeletedSelector(entity))
        {
            return Failure("soft_delete_state", "The soft-delete state was modified by the entity mutation.");
        }

        if (_deletedAtUtcSelector is not null
            && _deletedAtUtc != _deletedAtUtcSelector(entity))
        {
            return Failure("deleted_at", "The deletion timestamp was modified by the entity mutation.");
        }

        if (_concurrencyTokenSelector is not null
            && !Equals(_concurrencyToken, _concurrencyTokenSelector(entity)))
        {
            return Failure("concurrency_token", "The concurrency token was modified by the entity mutation.");
        }

        return DataResult.Succeeded(true);
    }

    private static DataResult<bool> Failure(string invariant, string reason)
        => DataResult.Failed<bool>(new MutationInvariantFailure(invariant, reason));
}
