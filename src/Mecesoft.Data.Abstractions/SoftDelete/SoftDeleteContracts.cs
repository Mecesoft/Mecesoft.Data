using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;

namespace Mecesoft.Data.Abstractions.SoftDelete;

/// <summary>
/// Exposes soft-delete state as domain metadata.
/// </summary>
/// <remarks>
/// Implementing this interface does not install a query filter or grant deleted-data access.
/// </remarks>
public interface ISoftDeletable
{
    /// <summary>
    /// Gets a value indicating whether the entity is soft-deleted.
    /// </summary>
    bool IsDeleted { get; }

    /// <summary>
    /// Gets the deletion timestamp in UTC when one is available.
    /// </summary>
    DateTimeOffset? DeletedAtUtc { get; }
}

/// <summary>
/// Identifies an operation governed by soft-delete authorization.
/// </summary>
public enum SoftDeleteOperation
{
    /// <summary>
    /// Reads deleted data.
    /// </summary>
    ReadDeleted,

    /// <summary>
    /// Marks active data as deleted.
    /// </summary>
    SoftDelete,

    /// <summary>
    /// Restores deleted data.
    /// </summary>
    Restore,

    /// <summary>
    /// Permanently deletes data.
    /// </summary>
    HardDelete,
}

/// <summary>
/// Supplies the soft-delete predicate independently of tenant policy.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
public interface ISoftDeletePolicy<TEntity>
    where TEntity : class, ISoftDeletable
{
    /// <summary>
    /// Gets the predicate that identifies deleted rows.
    /// </summary>
    Expression<Func<TEntity, bool>> IsDeletedPredicate { get; }
}

/// <summary>
/// Describes one resource-level soft-delete authorization request.
/// </summary>
/// <typeparam name="TEntity">The protected entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// The context carries data only. Possessing or constructing a context grants no authority. The
/// adapter must obtain <see cref="TenantId"/> from a resolver-issued scope and must load the entity
/// under tenant and soft-delete policies before calling the authorizer.
/// </remarks>
public sealed record SoftDeleteAuthorizationContext<TEntity, TKey, TTenantId>
    where TEntity : class, ISoftDeletable, ITenantEntity<TTenantId>
    where TKey : notnull
    where TTenantId : notnull
{
    /// <summary>
    /// Initializes a new authorization context.
    /// </summary>
    /// <param name="operation">The requested soft-delete operation.</param>
    /// <param name="entity">The loaded entity.</param>
    /// <param name="key">The entity key.</param>
    /// <param name="tenantId">The resolved tenant identifier.</param>
    public SoftDeleteAuthorizationContext(
        SoftDeleteOperation operation,
        TEntity entity,
        TKey key,
        TTenantId tenantId)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "The soft-delete operation is not defined.");
        }

        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tenantId);

        if (!EqualityComparer<TTenantId>.Default.Equals(entity.TenantId, tenantId))
        {
            throw new ArgumentException(
                "The entity does not belong to the resolved tenant.",
                nameof(entity));
        }

        Operation = operation;
        Entity = entity;
        Key = key;
        TenantId = tenantId;
    }

    /// <summary>
    /// Gets the requested soft-delete operation.
    /// </summary>
    public SoftDeleteOperation Operation { get; }

    /// <summary>
    /// Gets the loaded entity.
    /// </summary>
    public TEntity Entity { get; }

    /// <summary>
    /// Gets the entity key.
    /// </summary>
    public TKey Key { get; }

    /// <summary>
    /// Gets the resolved tenant identifier.
    /// </summary>
    public TTenantId TenantId { get; }
}

/// <summary>
/// Authorizes privileged soft-delete operations independently of tenant resolution.
/// </summary>
/// <typeparam name="TEntity">The protected entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// <para>
/// A successful permission never authorizes cross-tenant access. Adapters must resolve and apply
/// the required tenant scope before applying this independent permission.
/// </para>
/// <para>
/// The authorizer receives the loaded entity, key, and resolved tenant identifier, so it can make a
/// resource-level decision. The successful value echoes the requested operation as non-authoritative
/// data. Adapters must rely on <see cref="DataResult{T}.IsSuccess"/> for the decision, must verify the
/// echoed operation equals the context operation, and must not accept the echoed value from a caller
/// as proof of authorization.
/// </para>
/// </remarks>
public interface ISoftDeleteAuthorizer<TEntity, TKey, TTenantId>
    where TEntity : class, ISoftDeletable, ITenantEntity<TTenantId>
    where TKey : notnull
    where TTenantId : notnull
{
    /// <summary>
    /// Authorizes a resource-level soft-delete operation.
    /// </summary>
    /// <param name="context">The entity, key, tenant, and operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel authorization.</param>
    /// <returns>The authorized operation or a typed authorization failure.</returns>
    ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
        SoftDeleteAuthorizationContext<TEntity, TKey, TTenantId> context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines load-and-check soft-delete mutations without exposing hard delete.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
/// <typeparam name="TKey">The strongly typed entity key type.</typeparam>
/// <remarks>
/// <para>
/// Adapters must apply this order for both methods:
/// </para>
/// <list type="number">
/// <item><description>resolve the tenant scope for <see cref="TenantOperation.SoftDelete"/> or <see cref="TenantOperation.Restore"/>;</description></item>
/// <item><description>load the row using the tenant predicate composed with the soft-delete predicate;</description></item>
/// <item><description>authorize the matching <see cref="SoftDeleteOperation"/> with the loaded entity, key, and tenant identifier;</description></item>
/// <item><description>verify the concurrency token;</description></item>
/// <item><description>apply the state transition inside the active unit-of-work transaction and do not commit.</description></item>
/// </list>
/// <para>
/// A failure at any step must prevent persistence. Failures after the load phase must roll back the
/// active transaction and clear the change tracker; preflight failures, including missing
/// transaction or tenant resolution, must not execute SQL. The adapter must return
/// <see cref="TransactionRequiredFailure"/> when no transaction is active.
/// </para>
/// <para>
/// <see cref="SoftDeleteAsync"/> must use <c>SoftDeleteQueryPolicy.Compose</c> with
/// <c>requireDeleted: false</c>; <see cref="RestoreAsync"/> must use it with
/// <c>requireDeleted: true</c>. An adapter must not call <c>IgnoreQueryFilters</c> without
/// re-applying the tenant predicate in the same query. The abstraction cannot enforce this at runtime.
/// </para>
/// </remarks>
public interface ISoftDeleteRepository<TEntity, in TKey>
    where TEntity : class, ISoftDeletable
    where TKey : notnull
{
    /// <summary>
    /// Gets the unit of work that owns this repository's write boundary.
    /// </summary>
    IUnitOfWork UnitOfWork { get; }

    /// <summary>
    /// Loads an active entity by key, verifies concurrency, and marks it as deleted.
    /// </summary>
    /// <param name="key">The entity key.</param>
    /// <param name="expectedToken">The required optimistic concurrency token.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The updated entity or a typed failure.</returns>
    ValueTask<DataResult<TEntity>> SoftDeleteAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a deleted entity by key, authorizes restore, verifies concurrency, and restores it.
    /// </summary>
    /// <param name="key">The entity key.</param>
    /// <param name="expectedToken">The required optimistic concurrency token.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The restored entity or a typed failure.</returns>
    ValueTask<DataResult<TEntity>> RestoreAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default);
}
