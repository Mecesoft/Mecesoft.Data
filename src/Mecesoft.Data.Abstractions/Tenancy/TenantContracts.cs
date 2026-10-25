using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Tenancy;

/// <summary>
/// Exposes tenant ownership as domain metadata.
/// </summary>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// Implementing this interface does not enforce tenant isolation. Adapters must also use an
/// <see cref="ITenantAccessResolver{TEntity,TTenantId}"/> and inject its predicate independently
/// of caller specifications.
/// </remarks>
public interface ITenantEntity<out TTenantId>
    where TTenantId : notnull
{
    /// <summary>
    /// Gets the owning tenant identifier.
    /// </summary>
    TTenantId TenantId { get; }
}

/// <summary>
/// Identifies an operation for which tenant access must be resolved.
/// </summary>
public enum TenantOperation
{
    /// <summary>
    /// Reads data.
    /// </summary>
    Read,

    /// <summary>
    /// Creates data.
    /// </summary>
    Create,

    /// <summary>
    /// Updates data.
    /// </summary>
    Update,

    /// <summary>
    /// Soft-deletes data.
    /// </summary>
    SoftDelete,

    /// <summary>
    /// Restores soft-deleted data.
    /// </summary>
    Restore,

    /// <summary>
    /// Permanently deletes data.
    /// </summary>
    HardDelete,
}

/// <summary>
/// Carries a resolved tenant identifier and the operation for which it was resolved.
/// </summary>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// This is a non-authoritative data object. Possessing an instance does not grant access and an
/// instance must never be accepted as proof of authorization from a caller. Only an adapter may
/// consume a scope obtained from <see cref="ITenantAccessResolver{TEntity,TTenantId}"/> for the
/// current operation, and it must verify <see cref="Operation"/> matches that operation.
/// </remarks>
public sealed record TenantScope<TTenantId>
    where TTenantId : notnull
{
    /// <summary>
    /// Initializes a new tenant scope.
    /// </summary>
    /// <param name="tenantId">The resolved tenant identifier.</param>
    /// <param name="operation">The operation for which the scope was resolved.</param>
    public TenantScope(TTenantId tenantId, TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "The tenant operation is not defined.");
        }

        TenantId = tenantId;
        Operation = operation;
    }

    /// <summary>
    /// Gets the resolved tenant identifier.
    /// </summary>
    public TTenantId TenantId { get; }

    /// <summary>
    /// Gets the operation for which this scope was resolved.
    /// </summary>
    public TenantOperation Operation { get; }
}

/// <summary>
/// Resolves mandatory tenant scope and supplies the entity tenant-key selector.
/// </summary>
/// <typeparam name="TEntity">The protected entity type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// <para>
/// Implementations form a security boundary, but this interface does not enforce security by
/// itself. An adapter must:
/// </para>
/// <list type="number">
/// <item><description>call <see cref="ValidateConfiguration"/> during startup and refuse to serve when it fails;</description></item>
/// <item><description>call <see cref="ResolveRequiredScopeAsync"/> before any data command and execute no command on failure;</description></item>
/// <item><description>inject the scope's tenant predicate independently of caller specifications;</description></item>
/// <item><description>call <see cref="IsGraphInScopeAsync"/> after materialization/mutation and before persistence, including added or modified related entities.</description></item>
/// </list>
/// <para>
/// An unavailable, unauthenticated, unauthorized, or misconfigured scope must return a failed
/// result and the adapter must not execute a database command. There is no unrestricted fallback.
/// </para>
/// </remarks>
public interface ITenantAccessResolver<TEntity, TTenantId>
    where TEntity : class, ITenantEntity<TTenantId>
    where TTenantId : notnull
{
    /// <summary>
    /// Gets the selector that adapters use to inject a mandatory tenant predicate.
    /// </summary>
    Expression<Func<TEntity, TTenantId>> TenantIdSelector { get; }

    /// <summary>
    /// Validates that the tenant selector is present and structurally usable.
    /// </summary>
    /// <returns>Success when configured; otherwise, a <see cref="TenantConfigurationFailure"/>.</returns>
    /// <remarks>
    /// Adapters must call this method at startup and fail closed when it returns a failure. The
    /// default implementation only validates the selector shape; implementers may add checks for
    /// other required configuration.
    /// </remarks>
    DataResult<bool> ValidateConfiguration()
    {
        try
        {
            return TenantContractGuard.ValidateSelector(TenantIdSelector);
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure("The tenant selector could not be read."));
        }
    }

    /// <summary>
    /// Resolves and authorizes the required tenant scope for an operation.
    /// </summary>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel resolution.</param>
    /// <returns>A resolved scope or a typed failure. There is no unrestricted fallback.</returns>
    /// <remarks>
    /// Implementations must call <see cref="ValidateConfiguration"/> and fail closed with
    /// <see cref="TenantConfigurationFailure"/> when configuration is missing or invalid. They must
    /// not return a scope on configuration failure. The abstraction cannot enforce this at runtime.
    /// </remarks>
    ValueTask<DataResult<TenantScope<TTenantId>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a materialized entity against the current tenant scope.
    /// </summary>
    /// <param name="entity">The materialized entity.</param>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel resolution.</param>
    /// <returns>
    /// Success with <see langword="true"/> when the entity is in scope; success with
    /// <see langword="false"/> when the check completed and the entity is out of scope; or a typed
    /// failure when the scope cannot be resolved.
    /// </returns>
    /// <remarks>
    /// The entity is checked against a scope resolved by the implementation. Callers cannot supply
    /// a scope value, so a forged <see cref="TenantScope{TTenantId}"/> cannot be used as proof here.
    /// Adapters must treat both a failure and a <see langword="false"/> value as a denial.
    /// </remarks>
    async ValueTask<DataResult<bool>> IsInScopeAsync(
        TEntity entity,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        DataResult<TenantScope<TTenantId>> scopeResult =
            await ResolveRequiredScopeAsync(operation, cancellationToken).ConfigureAwait(false);

        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<bool>(scopeResult.Failure!);
        }

        TenantScope<TTenantId> scope = scopeResult.Value;
        if (scope.Operation != operation)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The resolved tenant scope is bound to a different operation."));
        }

        return DataResult.Succeeded(
            EqualityComparer<TTenantId>.Default.Equals(entity.TenantId, scope.TenantId));
    }

    /// <summary>
    /// Verifies the root entity and every reachable related entity against the current tenant scope.
    /// </summary>
    /// <param name="root">The root entity, including any changes made by a mutation.</param>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel graph traversal.</param>
    /// <returns>
    /// Success with <see langword="true"/> when the complete graph is in scope; success with
    /// <see langword="false"/> when the check completed and at least one related entity is out of
    /// scope; or a typed failure when the graph cannot be inspected.
    /// </returns>
    /// <remarks>
    /// The implementation must inspect the complete graph reachable from <paramref name="root"/>,
    /// including added, modified, and deleted related entities. It must return a failure rather than
    /// success when the graph cannot be inspected completely. Returning success without inspecting
    /// the graph violates this contract. Implementations should resolve the scope through
    /// <see cref="ResolveRequiredScopeAsync"/> so configuration validation is not bypassed. Adapters
    /// must treat both a failure and a <see langword="false"/> value as a denial and should map an
    /// out-of-scope result to <see cref="TenantOwnershipFailure"/>. The abstraction cannot enforce
    /// graph traversal at runtime.
    /// </remarks>
    ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        TEntity root,
        TenantOperation operation,
        CancellationToken cancellationToken = default);
}
