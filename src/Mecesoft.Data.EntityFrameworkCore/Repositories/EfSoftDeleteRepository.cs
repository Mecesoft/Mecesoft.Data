using System.Data.Common;
using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Concurrency;
using Mecesoft.Data.EntityFrameworkCore.Expressions;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Mecesoft.Data.EntityFrameworkCore.Repositories;

/// <summary>
/// Secure EF Core soft-delete repository for one tenant-owned, soft-deletable entity type.
/// </summary>
/// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
/// <typeparam name="TEntity">The soft-deletable tenant entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// <para>
/// Both operations require an active unit-of-work transaction and a resolved tenant scope. The
/// repository loads the row under the tenant predicate, authorizes the operation at resource level,
/// verifies the concurrency token, applies only the soft-delete state transition, and never commits.
/// </para>
/// <para>
/// Restore removes only the named soft-delete query filter through
/// <see cref="Queries.TenantQueryableExtensions.IgnoreSoftDelete{TEntity}"/> and composes an explicit
/// tenant predicate. The tenant filter is never removed. A failure after the load rolls back the
/// transaction and clears the change tracker.
/// </para>
/// </remarks>
public sealed class EfSoftDeleteRepository<TContext, TEntity, TKey, TTenantId>
    : ISoftDeleteRepository<TEntity, TKey>
    where TContext : MecesoftDbContext
    where TEntity : class, ISoftDeletable, ITenantEntity<TTenantId>
    where TKey : notnull
    where TTenantId : notnull
{
    private readonly TContext _context;
    private readonly TenantScopeCoordinator<TEntity, TTenantId> _tenantScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISoftDeleteAuthorizer<TEntity, TKey, TTenantId> _authorizer;
    private readonly Expression<Func<TEntity, TKey>> _keySelector;
    private readonly Func<TEntity, TKey> _compiledKeySelector;
    private readonly Func<TEntity, TTenantId> _tenantIdSelector;
    private readonly Expression<Func<TEntity, bool>> _isDeletedPredicate;
    private readonly Func<TEntity, bool> _isVisible;
    private readonly ConcurrencyTokenAccessor _concurrencyTokenAccessor;

    /// <summary>
    /// Initializes a secure soft-delete repository.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    /// <param name="tenantScope">The tenant resolver coordinator.</param>
    /// <param name="unitOfWork">The scoped unit of work.</param>
    /// <param name="authorizer">The resource-level soft-delete authorizer.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    public EfSoftDeleteRepository(
        TContext context,
        TenantScopeCoordinator<TEntity, TTenantId> tenantScope,
        IUnitOfWork unitOfWork,
        ISoftDeleteAuthorizer<TEntity, TKey, TTenantId> authorizer,
        Expression<Func<TEntity, TKey>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantScope);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(keySelector);

        KeySelectorValidator.Validate(context, keySelector);
        _context = context;
        _tenantScope = tenantScope;
        _unitOfWork = unitOfWork;
        _authorizer = authorizer;
        _keySelector = keySelector;
        _compiledKeySelector = keySelector.Compile();
        _tenantIdSelector = tenantScope.TenantIdSelector.Compile();
        var entityMetadata = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new TenantConfigurationException(
                $"The entity type '{typeof(TEntity).Name}' is not part of the DbContext model.");

        if (entityMetadata.FindProperty(nameof(ISoftDeletable.IsDeleted)) is null
            || entityMetadata.FindProperty(nameof(ISoftDeletable.DeletedAtUtc)) is null)
        {
            throw new TenantConfigurationException(
                $"The entity type '{typeof(TEntity).Name}' does not map the required soft-delete state properties.");
        }

        _isDeletedPredicate = context.GetSoftDeletePredicate<TEntity>();
        _isVisible = context.GetSoftDeleteVisibility<TEntity>();
        _concurrencyTokenAccessor = context.GetRequiredConcurrencyTokenAccessor(typeof(TEntity));
    }

    /// <inheritdoc />
    public IUnitOfWork UnitOfWork => _unitOfWork;

    /// <inheritdoc />
    public ValueTask<DataResult<TEntity>> SoftDeleteAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            key,
            expectedToken,
            SoftDeleteOperation.SoftDelete,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<TEntity>> RestoreAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            key,
            expectedToken,
            SoftDeleteOperation.Restore,
            cancellationToken);

    private async ValueTask<DataResult<TEntity>> TransitionAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        SoftDeleteOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedToken);

        string operationName = operation == SoftDeleteOperation.SoftDelete
            ? "soft_delete"
            : "restore";

        if (!HasActiveTransaction())
        {
            return DataResult.Failed<TEntity>(
                new TransactionRequiredFailure(operationName));
        }

        TenantOperation tenantOperation = operation == SoftDeleteOperation.SoftDelete
            ? TenantOperation.SoftDelete
            : TenantOperation.Restore;

        DataResult<TenantScope<TTenantId>> scopeResult;
        try
        {
            scopeResult = await _tenantScope
                .ResolveAsync(_context, tenantOperation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return DataResult.Failed<TEntity>(
                new ValidationFailure("The tenant resolution was cancelled."));
        }
        catch (Exception)
        {
            return DataResult.Failed<TEntity>(
                new TenantUnavailableFailure("The tenant could not be resolved."));
        }

        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<TEntity>(scopeResult.Failure!);
        }

        TTenantId tenantId = scopeResult.Value.TenantId;

        DataResult<bool> collation = _context.VerifyTenantCollation();
        if (collation.IsFailure)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(collation.Failure!);
        }

        EntityEntry<TEntity>? tracked;
        try
        {
            tracked = FindTracked(key);
        }
        catch (InvalidOperationException)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(
                new MutationInvariantFailure(
                    "primary_key",
                    "A protected key or relationship key was modified."));
        }

        if (tracked is not null && tracked.State != EntityState.Unchanged)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(
                new ValidationFailure(
                    "The entity already has pending changes. Commit or discard them before changing its soft-delete state."));
        }

        DataResult<TEntity> loadResult;
        try
        {
            loadResult = await LoadForTransitionAsync(
                key,
                tenantId,
                operation == SoftDeleteOperation.Restore,
                operation,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<TEntity>(
                new ValidationFailure("The soft-delete load was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure("The soft-delete load failed."))
                .ConfigureAwait(false);
        }

        if (loadResult.IsFailure)
        {
            return await AbortAsync<TEntity>(loadResult.Failure!)
                .ConfigureAwait(false);
        }

        TEntity entity = loadResult.Value;
        if (!EqualityComparer<TTenantId>.Default.Equals(
            _tenantIdSelector(entity),
            tenantId))
        {
            return await AbortAsync<TEntity>(
                new TenantOwnershipFailure(
                    "The loaded entity does not belong to the resolved tenant scope."))
                .ConfigureAwait(false);
        }

        Dictionary<object, EntityState> trackerSnapshot;
        try
        {
            trackerSnapshot = _context.ChangeTracker
                .Entries()
                .ToDictionary(
                    entry => entry.Entity,
                    entry => entry.State,
                    ReferenceEqualityComparer.Instance);
        }
        catch (Exception)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(
                new ValidationFailure(
                    "The change tracker is not in a valid state for a soft-delete operation."));
        }

        DataResult<SoftDeleteOperation> authorization;
        try
        {
            var authorizationContext =
                new SoftDeleteAuthorizationContext<TEntity, TKey, TTenantId>(
                    operation,
                    entity,
                    key,
                    tenantId);
            authorization = await _authorizer
                .AuthorizeAsync(authorizationContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<TEntity>(
                new ValidationFailure("The soft-delete authorization was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new AuthorizationFailure(
                    operationName,
                    "The soft-delete authorization failed."))
                .ConfigureAwait(false);
        }

        if (authorization.IsFailure)
        {
            return await AbortAsync<TEntity>(authorization.Failure!)
                .ConfigureAwait(false);
        }

        if (authorization.Value != operation)
        {
            return await AbortAsync<TEntity>(
                new AuthorizationFailure(
                    operationName,
                    "The authorizer returned a different operation."))
                .ConfigureAwait(false);
        }

        try
        {
            if (!TrackerIsUnchanged(trackerSnapshot))
            {
                return await AbortAsync<TEntity>(
                    new AuthorizationFailure(
                        operationName,
                        "The authorizer modified tracked state."))
                    .ConfigureAwait(false);
            }

            EntityMutationGuard<TEntity> guard = CaptureMutationGuard(entity);
            DataResult<bool> guardResult = guard.Validate(entity);
            if (guardResult.IsFailure)
            {
                return await AbortAsync<TEntity>(guardResult.Failure!)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new AuthorizationFailure(
                    operationName,
                    "The authorizer left the entity in an invalid state."))
                .ConfigureAwait(false);
        }

        ConcurrencyToken currentToken;
        try
        {
            currentToken = _concurrencyTokenAccessor.Read(entity);
        }
        catch (ConcurrencyConfigurationException exception)
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyConfigurationFailure(exception.Message))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyConfigurationFailure(
                    "The concurrency token could not be read."))
                .ConfigureAwait(false);
        }

        if (!string.Equals(
            currentToken.Value,
            expectedToken.Value,
            StringComparison.Ordinal))
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyFailure<TKey>(
                    typeof(TEntity).Name,
                    key,
                    expectedToken,
                    currentToken))
                .ConfigureAwait(false);
        }

        return await ApplyTransitionAsync(
            entity,
            operation,
            operationName,
            expectedToken,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<DataResult<TEntity>> ApplyTransitionAsync(
        TEntity entity,
        SoftDeleteOperation operation,
        string operationName,
        ConcurrencyToken expectedToken,
        CancellationToken cancellationToken)
    {
        EntityEntry<TEntity> entry = _context.Entry(entity);
        string isDeletedName = nameof(ISoftDeletable.IsDeleted);
        string deletedAtName = nameof(ISoftDeletable.DeletedAtUtc);

        if (entry.Metadata.FindProperty(isDeletedName) is null
            || entry.Metadata.FindProperty(deletedAtName) is null)
        {
            return await AbortAsync<TEntity>(
                new TenantConfigurationFailure(
                    "The soft-delete state properties are not mapped for this entity."))
                .ConfigureAwait(false);
        }

        DateTimeOffset? deletedAtUtc = operation == SoftDeleteOperation.SoftDelete
            ? DateTimeOffset.UtcNow
            : null;

        try
        {
            entry.Property(isDeletedName).CurrentValue =
                operation == SoftDeleteOperation.SoftDelete;
            entry.Property(deletedAtName).CurrentValue = deletedAtUtc;

            _context.AuthorizeSoftDeleteTransition(
                entity,
                operation,
                deletedAtUtc);

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<TEntity>(
                new ValidationFailure("The soft-delete persistence was cancelled."))
                .ConfigureAwait(false);
        }
        catch (SaveChangesSecurityException exception)
        {
            return await AbortAsync<TEntity>(exception.Failure)
                .ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyFailure<TKey>(
                    typeof(TEntity).Name,
                    _compiledKeySelector(entity),
                    expectedToken))
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure(
                    $"The {operationName} operation failed."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure(
                    $"The {operationName} operation failed."))
                .ConfigureAwait(false);
        }
        finally
        {
            _context.ClearSoftDeleteTransition();
        }

        bool transitionApplied = operation == SoftDeleteOperation.SoftDelete
            ? entity.IsDeleted && entity.DeletedAtUtc is not null
            : !entity.IsDeleted && entity.DeletedAtUtc is null;

        if (!transitionApplied)
        {
            return await AbortAsync<TEntity>(
                new MutationInvariantFailure(
                    "soft_delete_transition",
                    "The soft-delete transition did not produce the required state."))
                .ConfigureAwait(false);
        }

        bool visibilityCorrect;
        try
        {
            bool isVisible = _isVisible(entity);
            visibilityCorrect = operation == SoftDeleteOperation.SoftDelete
                ? !isVisible
                : isVisible;
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new TenantConfigurationFailure(
                    "The configured soft-delete visibility predicate cannot be evaluated."))
                .ConfigureAwait(false);
        }

        if (!visibilityCorrect)
        {
            return await AbortAsync<TEntity>(
                new TenantConfigurationFailure(
                    "The configured soft-delete visibility predicate does not reflect the state transition."))
                .ConfigureAwait(false);
        }

        try
        {
            _ = _concurrencyTokenAccessor.Read(entity);
        }
        catch (ConcurrencyConfigurationException exception)
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyConfigurationFailure(exception.Message))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new ConcurrencyConfigurationFailure(
                    "The concurrency token could not be read after persistence."))
                .ConfigureAwait(false);
        }

        return DataResult.Succeeded(entity);
    }

    private async ValueTask<DataResult<TEntity>> LoadForTransitionAsync(
        TKey key,
        TTenantId tenantId,
        bool requireDeleted,
        SoftDeleteOperation operation,
        CancellationToken cancellationToken)
    {
        using IDisposable? readAuthorization = requireDeleted
            ? SoftDeleteReadAuthorization.Begin(tenantId, operation)
            : null;
        TenantOperation tenantOperation = operation == SoftDeleteOperation.SoftDelete
            ? TenantOperation.SoftDelete
            : TenantOperation.Restore;
        using IDisposable filterScope = _context.BeginInternalTenantFilterScope(
            tenantId,
            tenantOperation);

        IQueryable<TEntity> query = _context.Set<TEntity>();
        if (requireDeleted)
        {
            query = query.IgnoreSoftDelete();
        }

        Expression<Func<TEntity, bool>> keyPredicate = BuildKeyPredicate(key);
        Expression<Func<TEntity, bool>> policyPredicate =
            SoftDeleteQueryPolicy.Compose(
                _tenantScope.TenantIdSelector,
                tenantId,
                _isDeletedPredicate,
                requireDeleted);

        TEntity? entity = await query
            .AsNoTracking()
            .Where(keyPredicate)
            .Where(policyPredicate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return DataResult.Failed<TEntity>(
                new NotFoundFailure<TKey>(typeof(TEntity).Name, key));
        }

        if (!TenantValueGuard.IsCanonical(entity.TenantId, typeof(TTenantId)))
        {
            return DataResult.Failed<TEntity>(
                new TenantOwnershipFailure(
                    "The persisted entity tenant is not a canonical string tenant identifier."));
        }

        EntityEntry<TEntity>? tracked = FindTracked(key);
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        _context.Attach(entity);
        return DataResult.Succeeded(entity);
    }

    private bool HasActiveTransaction()
    {
        return _unitOfWork.HasActiveTransaction
            && _unitOfWork.CurrentTransaction?.State == DataTransactionState.Active
            && _context.Database.CurrentTransaction is not null;
    }

    private bool TrackerIsUnchanged(
        Dictionary<object, EntityState> snapshot)
    {
        EntityEntry[] entries = _context.ChangeTracker.Entries().ToArray();
        if (entries.Length != snapshot.Count)
        {
            return false;
        }

        foreach (EntityEntry entry in entries)
        {
            if (!snapshot.TryGetValue(entry.Entity, out EntityState state)
                || state != entry.State)
            {
                return false;
            }
        }

        return true;
    }

    private EntityEntry<TEntity>? FindTracked(TKey key)
    {
        foreach (EntityEntry<TEntity> entry in _context.ChangeTracker.Entries<TEntity>())
        {
            if (EqualityComparer<TKey>.Default.Equals(
                _compiledKeySelector(entry.Entity),
                key))
            {
                return entry;
            }
        }

        return null;
    }

    private EntityMutationGuard<TEntity> CaptureMutationGuard(TEntity entity)
    {
        return new EntityMutationGuard<TEntity>(
            entity,
            primaryKeySelector: item => _compiledKeySelector(item),
            tenantIdSelector: item => _tenantIdSelector(item),
            isDeletedSelector: item => item.IsDeleted,
            deletedAtUtcSelector: item => item.DeletedAtUtc,
            concurrencyTokenSelector: _concurrencyTokenAccessor.Read);
    }

    private Expression<Func<TEntity, bool>> BuildKeyPredicate(TKey key)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "entity");
        var replacer = new ExpressionParameterReplacer(_keySelector.Parameters[0], parameter);
        Expression keyBody = replacer.Visit(_keySelector.Body);
        Expression body = Expression.Equal(
            keyBody,
            Expression.Constant(key, typeof(TKey)));
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    private async ValueTask<DataResult<T>> AbortAsync<T>(DataFailure failure)
    {
        _tenantScope.ClearWriteAuthorization();

        DataResult<TransactionCompletion> rollback = await _unitOfWork
            .RollbackAsync(CancellationToken.None)
            .ConfigureAwait(false);

        return rollback.IsFailure
            ? DataResult.Failed<T>(rollback.Failure!)
            : DataResult.Failed<T>(failure);
    }
}
