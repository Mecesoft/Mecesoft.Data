using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Coordinates tenant resolution and query execution for one tenant entity type.
/// </summary>
/// <typeparam name="TEntity">The tenant entity type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// <para>
/// The coordinator validates resolver configuration, resolves the required scope, verifies the
/// returned scope is bound to the requested operation, and stores the tenant identifier in the
/// scoped <see cref="ITenantScopeStore"/> before any query is executed.
/// </para>
/// <para>
/// <c>ExecuteAsync</c> is a fail-closed gate for future repository implementations:
/// the query delegate is not invoked when resolution fails. This class does not execute the query or
/// enforce authorization by itself; the adapter must still use the DbContext filters.
/// </para>
/// </remarks>
public sealed class TenantScopeCoordinator<TEntity, TTenantId>
    where TEntity : class, ITenantEntity<TTenantId>
    where TTenantId : notnull
{
    private readonly ITenantAccessResolver<TEntity, TTenantId> _resolver;
    private readonly ITenantScopeStore _store;

    /// <summary>
    /// Initializes a new coordinator.
    /// </summary>
    /// <param name="resolver">The application-provided tenant resolver.</param>
    /// <param name="store">The scoped tenant store shared with the DbContext.</param>
    public TenantScopeCoordinator(
        ITenantAccessResolver<TEntity, TTenantId> resolver,
        ITenantScopeStore store)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(store);
        _resolver = resolver;
        _store = store;
    }

    /// <summary>
    /// Gets the resolver's tenant selector for in-memory guard and ownership checks.
    /// </summary>
    public Expression<Func<TEntity, TTenantId>> TenantIdSelector => _resolver.TenantIdSelector;

    /// <summary>
    /// Validates configuration and resolves the required tenant scope.
    /// </summary>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel resolution.</param>
    /// <returns>The resolved scope or a typed failure.</returns>
    public ValueTask<DataResult<TenantScope<TTenantId>>> ResolveAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
        => ResolveCoreAsync(
            context: null,
            operation,
            cancellationToken);

    /// <summary>
    /// Validates configuration, resolves the required tenant scope, and binds a write
    /// authorization to the supplied context instance.
    /// </summary>
    /// <param name="context">The context that will consume the write authorization.</param>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel resolution.</param>
    /// <returns>The resolved scope or a typed failure.</returns>
    /// <remarks>
    /// Binding the grant to the context prevents another context sharing the same scoped store
    /// from replaying the authorization. Read operations do not produce a write grant; use
    /// <see cref="ExecuteAsync{TResult}(MecesoftDbContext, TenantOperation, Func{CancellationToken, ValueTask{TResult}}, CancellationToken)"/>
    /// for direct reads.
    /// </remarks>
    public async ValueTask<DataResult<TenantScope<TTenantId>>> ResolveAsync(
        MecesoftDbContext context,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await ResolveCoreAsync(
            context,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DataResult<TenantScope<TTenantId>>> ResolveCoreAsync(
        MecesoftDbContext? context,
        TenantOperation operation,
        CancellationToken cancellationToken)
    {
        DataResult<bool> configuration = _resolver.ValidateConfiguration();
        if (configuration.IsFailure)
        {
            return DataResult.Failed<TenantScope<TTenantId>>(configuration.Failure!);
        }

        DataResult<TenantScope<TTenantId>> scopeResult =
            await _resolver.ResolveRequiredScopeAsync(operation, cancellationToken).ConfigureAwait(false);

        if (scopeResult.IsFailure)
        {
            return scopeResult;
        }

        TenantScope<TTenantId> scope = scopeResult.Value;
        if (scope.Operation != operation)
        {
            return DataResult.Failed<TenantScope<TTenantId>>(
                new TenantConfigurationFailure(
                    "The resolved tenant scope is bound to a different operation."));
        }

        if (scope.TenantId is string text && !TenantIdValidation.IsCanonical(text))
        {
            return DataResult.Failed<TenantScope<TTenantId>>(
                new ValidationFailure(
                    "The resolved tenant identifier is not canonical. "
                    + $"String tenant identifiers must be non-empty, contain at most "
                    + $"{TenantIdValidation.MaxStringLength} UTF-16 code units, and must not "
                    + "start or end with whitespace."));
        }

        TenantWriteAuthorization? existingAuthorization =
            operation == TenantOperation.Read
                ? null
                : TenantWriteAuthorizationRegistry.Get(_store);
        bool preserveContextBoundGrant = context is null
            && existingAuthorization?.Context is not null
            && existingAuthorization.Operation == operation
            && Equals(existingAuthorization.TenantId, scope.TenantId)
            && Equals(_store.TenantId, scope.TenantId)
            && _store.Operation == operation;

        if (!preserveContextBoundGrant
            && !_store.TrySet(scope.TenantId, operation))
        {
            return DataResult.Failed<TenantScope<TTenantId>>(
                new AuthorizationFailure(
                    "tenant_scope",
                    "The resolved tenant conflicts with the current tenant scope."));
        }

        if (operation != TenantOperation.Read)
        {
            TenantWriteAuthorizationRegistry.Set(
                _store,
                new TenantWriteAuthorization(
                    scope.TenantId,
                    operation,
                    context));
        }

        return scopeResult;
    }

    /// <summary>
    /// Resolves the tenant scope and invokes a query delegate only when resolution succeeds.
    /// </summary>
    /// <typeparam name="TResult">The query result type.</typeparam>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="query">The query to execute after the tenant store has been set.</param>
    /// <param name="cancellationToken">The token used to cancel resolution and the query.</param>
    /// <returns>The query result or the resolution failure.</returns>
    public async ValueTask<DataResult<TResult>> ExecuteAsync<TResult>(
        TenantOperation operation,
        Func<CancellationToken, ValueTask<TResult>> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        DataResult<TenantScope<TTenantId>> scopeResult =
            await ResolveAsync(operation, cancellationToken).ConfigureAwait(false);

        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<TResult>(scopeResult.Failure!);
        }

        try
        {
            TResult value = await query(cancellationToken).ConfigureAwait(false);
            return DataResult.Succeeded(value);
        }
        catch
        {
            ClearWriteAuthorization();
            throw;
        }
    }

    /// <summary>
    /// Resolves a read scope, binds it to the supplied context instance, and invokes the query
    /// delegate only after authorization succeeds.
    /// </summary>
    /// <typeparam name="TResult">The query result type.</typeparam>
    /// <param name="context">The context instance that will execute the read query.</param>
    /// <param name="operation">The operation; only <see cref="TenantOperation.Read"/> is accepted.</param>
    /// <param name="query">The query to execute after the read scope is authorized.</param>
    /// <param name="cancellationToken">The token used to cancel resolution and the query.</param>
    /// <returns>The query result or a typed authorization failure.</returns>
    /// <remarks>
    /// This overload is the supported way to authorize direct DbSet reads. The grant is bound to
    /// the supplied context instance and cannot be reused by another context.
    /// </remarks>
    public async ValueTask<DataResult<TResult>> ExecuteAsync<TResult>(
        MecesoftDbContext context,
        TenantOperation operation,
        Func<CancellationToken, ValueTask<TResult>> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);

        if (operation != TenantOperation.Read)
        {
            return DataResult.Failed<TResult>(
                new ValidationFailure(
                    "Direct query authorization is only supported for read operations."));
        }

        DataResult<TenantScope<TTenantId>> scopeResult =
            await ResolveAsync(operation, cancellationToken).ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<TResult>(scopeResult.Failure!);
        }

        try
        {
            context.AuthorizeReadScope(
                scopeResult.Value.TenantId,
                operation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.ClearReadAuthorization();
            return DataResult.Failed<TResult>(
                new AuthorizationFailure(
                    "read_scope",
                    exception.Message));
        }

        try
        {
            TResult value = await query(cancellationToken).ConfigureAwait(false);
            return DataResult.Succeeded(value);
        }
        finally
        {
            context.ClearReadAuthorization();
        }
    }

    /// <summary>
    /// Captures protected entity state with the tenant selector supplied by this coordinator.
    /// </summary>
    /// <param name="entity">The entity before mutation.</param>
    /// <param name="primaryKeySelector">The primary-key selector.</param>
    /// <param name="isDeletedSelector">The soft-delete flag selector, when applicable.</param>
    /// <param name="deletedAtUtcSelector">The deletion timestamp selector, when applicable.</param>
    /// <param name="concurrencyTokenSelector">The concurrency-token selector, when applicable.</param>
    /// <returns>The mutation guard to validate after the mutation.</returns>
    public EntityMutationGuard<TEntity> CaptureMutation(
        TEntity entity,
        Func<TEntity, object?> primaryKeySelector,
        Func<TEntity, bool>? isDeletedSelector = null,
        Func<TEntity, DateTimeOffset?>? deletedAtUtcSelector = null,
        Func<TEntity, ConcurrencyToken?>? concurrencyTokenSelector = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(primaryKeySelector);

        return new EntityMutationGuard<TEntity>(
            entity,
            primaryKeySelector,
            tenantIdSelector: item => item.TenantId,
            isDeletedSelector,
            deletedAtUtcSelector,
            concurrencyTokenSelector);
    }

    /// <summary>
    /// Validates mutation invariants, current entity ownership, and graph ownership before persistence.
    /// </summary>
    /// <param name="entity">The entity after mutation.</param>
    /// <param name="mutationGuard">The guard captured before mutation.</param>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel validation.</param>
    /// <returns>Success with <see langword="true"/> when all checks pass; otherwise, a typed failure.</returns>
    /// <remarks>
    /// This is an integration point, not a runtime enforcement mechanism. The adapter must call it
    /// after mutation and before persistence and must not persist on failure.
    /// </remarks>
    public async ValueTask<DataResult<bool>> ValidateMutationAsync(
        TEntity entity,
        EntityMutationGuard<TEntity> mutationGuard,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mutationGuard);

        DataResult<bool> invariantResult = mutationGuard.Validate(entity);
        if (invariantResult.IsFailure)
        {
            return invariantResult;
        }

        DataResult<bool> entityScope = await ValidateEntityScopeAsync(
            entity,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (entityScope.IsFailure)
        {
            return entityScope;
        }

        DataResult<bool> graphScope = await ValidateGraphAsync(
            entity,
            operation,
            cancellationToken).ConfigureAwait(false);
        return graphScope;
    }

    /// <summary>
    /// Validates the entity graph against the current tenant scope.
    /// </summary>
    /// <param name="root">The root entity graph.</param>
    /// <param name="operation">The operation being authorized.</param>
    /// <param name="cancellationToken">The token used to cancel validation.</param>
    /// <returns>Success with <see langword="true"/> when the graph is in scope; otherwise, a typed failure.</returns>
    public async ValueTask<DataResult<bool>> ValidateGraphAsync(
        TEntity root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        DataResult<TenantScope<TTenantId>> scopeResult =
            await ResolveAsync(operation, cancellationToken).ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<bool>(scopeResult.Failure!);
        }

        DataResult<bool> graphScope;
        try
        {
            graphScope = await _resolver
                .IsGraphInScopeAsync(root, operation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ClearWriteAuthorization();
            throw;
        }

        if (graphScope.IsFailure)
        {
            ClearWriteAuthorization();
            return graphScope;
        }

        if (!graphScope.Value)
        {
            ClearWriteAuthorization();
            return DataResult.Failed<bool>(
                new TenantOwnershipFailure("The entity graph is outside the resolved tenant scope."));
        }

        return graphScope;
    }

    /// <summary>
    /// Clears any write authorization left by a failed repository or graph validation.
    /// </summary>
    internal void ClearWriteAuthorization()
    {
        TenantWriteAuthorizationRegistry.Clear(_store);
    }

    private async ValueTask<DataResult<bool>> ValidateEntityScopeAsync(
        TEntity entity,
        TenantOperation operation,
        CancellationToken cancellationToken)
    {
        DataResult<TenantScope<TTenantId>> scopeResult =
            await ResolveAsync(operation, cancellationToken).ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<bool>(scopeResult.Failure!);
        }

        DataResult<bool> entityScope;
        try
        {
            entityScope = await _resolver
                .IsInScopeAsync(entity, operation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ClearWriteAuthorization();
            throw;
        }

        if (entityScope.IsFailure)
        {
            ClearWriteAuthorization();
            return entityScope;
        }

        if (!entityScope.Value)
        {
            ClearWriteAuthorization();
            return DataResult.Failed<bool>(
                new TenantOwnershipFailure("The entity is outside the resolved tenant scope."));
        }

        return entityScope;
    }

}
