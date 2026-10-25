using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Transactions;

namespace Mecesoft.Data.Abstractions.Repositories;

/// <summary>
/// Marks a mutation that completed successfully.
/// </summary>
/// <remarks>
/// The mutation mutates the entity in place. This marker exists so the mutation can return a typed
/// failure without returning or replacing the entity. Adapters must ignore the success value.
/// </remarks>
public sealed class EntityMutationResult
{
    private EntityMutationResult()
    {
    }

    /// <summary>
    /// Gets the successful mutation marker.
    /// </summary>
    public static EntityMutationResult Completed { get; } = new();
}

/// <summary>
/// Describes a successful update and the current optimistic concurrency token.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed record EntityUpdateResult<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Initializes a new update result.
    /// </summary>
    /// <param name="entity">The mutated entity.</param>
    /// <param name="concurrencyToken">The current token after the update.</param>
    public EntityUpdateResult(TEntity entity, ConcurrencyToken concurrencyToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(concurrencyToken);
        Entity = entity;
        ConcurrencyToken = concurrencyToken;
    }

    /// <summary>
    /// Gets the mutated entity.
    /// </summary>
    public TEntity Entity { get; }

    /// <summary>
    /// Gets the current token after the update.
    /// </summary>
    public ConcurrencyToken ConcurrencyToken { get; }
}

/// <summary>
/// Applies a mutation to an entity loaded by a repository inside the current unit of work.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <param name="entity">The policy-scoped, current entity loaded by the adapter.</param>
/// <param name="cancellationToken">The token used to cancel mutation work.</param>
/// <returns>
/// <see cref="EntityMutationResult.Completed"/> on success, or a typed failure such as
/// <see cref="ValidationFailure"/> that the adapter must propagate without persisting.
/// </returns>
/// <remarks>
/// <para>
/// The delegate does not enforce invariants by itself. Before invoking it, the adapter must capture
/// protected state with <see cref="EntityMutationGuard{TEntity}"/>; after it returns successfully,
/// the adapter must call the guard and must not persist when validation fails.
/// </para>
/// <para>
/// Protected state includes the tenant identifier, primary key, soft-delete flags, deletion
/// timestamp, and concurrency token. The adapter must also validate related-entity tenant ownership
/// before persistence. Mutations must not be used to perform soft delete or restore; those
/// operations have their own authorization contracts.
/// </para>
/// </remarks>
public delegate ValueTask<DataResult<EntityMutationResult>> EntityMutation<in TEntity>(
    TEntity entity,
    CancellationToken cancellationToken)
    where TEntity : class;

/// <summary>
/// Defines provider-independent entity creation and load-before-mutate operations.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The strongly typed entity key type.</typeparam>
/// <remarks>
/// This contract deliberately excludes detached update, detached delete, and hard-delete operations.
/// Implementations must apply tenant policies before loading or mutating an entity.
/// </remarks>
public interface IRepository<TEntity, in TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class
    where TKey : notnull
{
    /// <summary>
    /// Gets the unit of work that owns this repository's write boundary.
    /// </summary>
    IUnitOfWork UnitOfWork { get; }

    /// <summary>
    /// Adds an entity after mandatory tenant ownership validation.
    /// </summary>
    /// <param name="entity">The new entity.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The accepted entity or a typed failure.</returns>
    /// <remarks>
    /// <para>
    /// The adapter must resolve the required tenant scope before accepting the entity and must
    /// validate that the entity and every reachable related entity belong to that scope.
    /// </para>
    /// <para>
    /// This write method must run inside an active <see cref="IUnitOfWork"/> transaction. The
    /// adapter must return
    /// <see cref="TransactionRequiredFailure"/> when no transaction is active. The command executes
    /// within the transaction but must not commit; generated keys may be returned because the command
    /// has executed, but they are not durable until <see cref="IUnitOfWork.CommitAsync"/> succeeds.
    /// </para>
    /// </remarks>
    ValueTask<DataResult<TEntity>> AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads an entity by key, verifies its concurrency token, and applies a mutation.
    /// </summary>
    /// <param name="key">The key of the entity to load under mandatory policies.</param>
    /// <param name="expectedToken">The required optimistic concurrency token.</param>
    /// <param name="mutation">The mutation applied only after the current entity is loaded.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// The mutated entity and its current concurrency token, or a typed failure such as not-found,
    /// tenant, validation, concurrency, or transaction-required.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The adapter must apply this order: tenant resolution, load under tenant and soft-delete
    /// policies, concurrency verification, mutation-guard capture, mutation, mutation-result
    /// propagation, invariant validation, related-entity tenant validation, then persistence.
    /// </para>
    /// <para>
    /// A mutation failure must be returned unchanged and must not be persisted. The adapter must
    /// return <see cref="TransactionRequiredFailure"/> when no transaction is active. The command
    /// executes within the active transaction but must not commit; the returned concurrency token is
    /// the current token after the update and becomes durable only when the unit of work commits.
    /// </para>
    /// </remarks>
    ValueTask<DataResult<EntityUpdateResult<TEntity>>> UpdateAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        EntityMutation<TEntity> mutation,
        CancellationToken cancellationToken = default);
}
