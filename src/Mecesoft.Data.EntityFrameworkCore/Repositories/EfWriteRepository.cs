using System.Data.Common;
using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Concurrency;
using Mecesoft.Data.EntityFrameworkCore.Expressions;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Mecesoft.Data.EntityFrameworkCore.Repositories;

/// <summary>
/// Secure EF Core write repository for one tenant-owned entity type.
/// </summary>
/// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
/// <typeparam name="TEntity">The tenant entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// Every write resolves the tenant scope, requires an active unit-of-work transaction, loads updates
/// under the tenant filter, protects mutation invariants, validates graph ownership, and never commits.
/// SaveChanges central validation remains active even when the repository is bypassed.
/// On failure, the transaction is rolled back and the change tracker is cleared.
/// </remarks>
public sealed class EfWriteRepository<TContext, TEntity, TKey, TTenantId>
    : IRepository<TEntity, TKey>
    where TContext : MecesoftDbContext
    where TEntity : class, ITenantEntity<TTenantId>
    where TKey : notnull
    where TTenantId : notnull
{
    private readonly TContext _context;
    private readonly TenantScopeCoordinator<TEntity, TTenantId> _tenantScope;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Expression<Func<TEntity, TKey>> _keySelector;
    private readonly Func<TEntity, TKey> _compiledKeySelector;
    private readonly Func<TEntity, TTenantId> _tenantIdSelector;
    private readonly ConcurrencyTokenAccessor _concurrencyTokenAccessor;
    private readonly EfReadRepository<TContext, TEntity, TKey, TTenantId> _readRepository;
    private readonly bool _isSoftDeletable;

    /// <summary>
    /// Initializes a secure write repository.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    /// <param name="tenantScope">The tenant resolver coordinator.</param>
    /// <param name="unitOfWork">The scoped unit of work.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    public EfWriteRepository(
        TContext context,
        TenantScopeCoordinator<TEntity, TTenantId> tenantScope,
        IUnitOfWork unitOfWork,
        Expression<Func<TEntity, TKey>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantScope);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(keySelector);

        KeySelectorValidator.Validate(context, keySelector);
        _context = context;
        _tenantScope = tenantScope;
        _unitOfWork = unitOfWork;
        _keySelector = keySelector;
        _compiledKeySelector = keySelector.Compile();
        _tenantIdSelector = tenantScope.TenantIdSelector.Compile();
        _concurrencyTokenAccessor = context.GetRequiredConcurrencyTokenAccessor(typeof(TEntity));
        _readRepository = new EfReadRepository<TContext, TEntity, TKey, TTenantId>(
            context,
            tenantScope,
            keySelector);
        _isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity));
    }

    /// <inheritdoc />
    public IUnitOfWork UnitOfWork => _unitOfWork;

    /// <inheritdoc />
    public ValueTask<DataResult<TEntity>> GetByIdAsync(
        TKey key,
        CancellationToken cancellationToken = default)
        => _readRepository.GetByIdAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<TResult>> GetByIdAsync<TResult>(
        TKey key,
        IProjection<TEntity, TResult> projection,
        CancellationToken cancellationToken = default)
        => _readRepository.GetByIdAsync(key, projection, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<TEntity>> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.FirstOrDefaultAsync(specification, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<TResult>> FirstOrDefaultAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.FirstOrDefaultAsync(specification, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<IReadOnlyList<TEntity>>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.ListAsync(specification, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<IReadOnlyList<TResult>>> ListAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.ListAsync(specification, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<PageResult<TResult>>> PageAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        PageRequest pageRequest,
        CancellationToken cancellationToken = default)
        => _readRepository.PageAsync(specification, pageRequest, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<long>> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.CountAsync(specification, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DataResult<bool>> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
        => _readRepository.AnyAsync(specification, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<DataResult<TEntity>> AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        DataResult<TEntity>? transactionFailure = EnsureActiveTransaction<TEntity>();
        if (transactionFailure is not null)
        {
            return transactionFailure;
        }

        DataResult<TenantScope<TTenantId>> scopeResult = await _tenantScope
            .ResolveAsync(_context, TenantOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<TEntity>(scopeResult.Failure!);
        }

        DataResult<bool> collation = _context.VerifyTenantCollation();
        if (collation.IsFailure)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(collation.Failure!);
        }

        TTenantId entityTenantId = _tenantIdSelector(entity);
        if (!TenantValueGuard.IsCanonical(entityTenantId, typeof(TTenantId)))
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(
                new TenantOwnershipFailure(
                    "The entity tenant is not a canonical string tenant identifier."));
        }

        if (!EqualityComparer<TTenantId>.Default.Equals(
            entityTenantId,
            scopeResult.Value.TenantId))
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(
                new TenantOwnershipFailure(
                    "The entity tenant does not match the resolved tenant scope."));
        }

        DataResult<bool> graphValidation = await _tenantScope
            .ValidateGraphAsync(entity, TenantOperation.Create, cancellationToken)
            .ConfigureAwait(false);
        if (graphValidation.IsFailure)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<TEntity>(graphValidation.Failure!);
        }

        try
        {
            _context.Set<TEntity>().Add(entity);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DataResult.Succeeded(entity);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<TEntity>(
                new ValidationFailure("The add operation was cancelled."))
                .ConfigureAwait(false);
        }
        catch (SaveChangesSecurityException exception)
        {
            return await AbortAsync<TEntity>(exception.Failure).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure("The add operation encountered a concurrency conflict."))
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure("The add operation failed."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<TEntity>(
                new PersistenceFailure("The add operation failed."))
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<EntityUpdateResult<TEntity>>> UpdateAsync(
        TKey key,
        ConcurrencyToken expectedToken,
        EntityMutation<TEntity> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expectedToken);
        ArgumentNullException.ThrowIfNull(mutation);

        DataResult<EntityUpdateResult<TEntity>>? transactionFailure =
            EnsureActiveTransaction<EntityUpdateResult<TEntity>>();
        if (transactionFailure is not null)
        {
            return transactionFailure;
        }

        DataResult<TenantScope<TTenantId>> scopeResult = await _tenantScope
            .ResolveAsync(_context, TenantOperation.Update, cancellationToken)
            .ConfigureAwait(false);
        if (scopeResult.IsFailure)
        {
            return DataResult.Failed<EntityUpdateResult<TEntity>>(scopeResult.Failure!);
        }

        DataResult<bool> collation = _context.VerifyTenantCollation();
        if (collation.IsFailure)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<EntityUpdateResult<TEntity>>(collation.Failure!);
        }

        DataResult<TEntity> loadResult;
        using (_context.BeginInternalTenantFilterScope(
            scopeResult.Value.TenantId,
            TenantOperation.Update))
        {
            loadResult = await LoadForUpdateAsync(key, cancellationToken)
                .ConfigureAwait(false);
        }
        if (loadResult.IsFailure)
        {
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<EntityUpdateResult<TEntity>>(loadResult.Failure!);
        }

        TEntity entity = loadResult.Value;
        ConcurrencyToken currentToken;
        try
        {
            currentToken = _concurrencyTokenAccessor.Read(entity);
        }
        catch (ConcurrencyConfigurationException exception)
        {
            Detach(entity);
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<EntityUpdateResult<TEntity>>(
                new ConcurrencyConfigurationFailure(exception.Message));
        }

        if (!string.Equals(currentToken.Value, expectedToken.Value, StringComparison.Ordinal))
        {
            Detach(entity);
            _tenantScope.ClearWriteAuthorization();
            return DataResult.Failed<EntityUpdateResult<TEntity>>(
                new ConcurrencyFailure<TKey>(
                    typeof(TEntity).Name,
                    key,
                    expectedToken,
                    currentToken));
        }

        EntityMutationGuard<TEntity> mutationGuard = CaptureMutationGuard(entity);

        DataResult<EntityMutationResult> mutationResult;
        try
        {
            mutationResult = await mutation(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new ValidationFailure("The update mutation was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new PersistenceFailure("The update mutation failed."))
                .ConfigureAwait(false);
        }

        if (mutationResult.IsFailure)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(mutationResult.Failure!)
                .ConfigureAwait(false);
        }

        DataResult<bool> validation;
        try
        {
            validation = await _tenantScope
                .ValidateMutationAsync(
                    entity,
                    mutationGuard,
                    TenantOperation.Update,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new ValidationFailure("The update validation was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new PersistenceFailure("The update validation failed."))
                .ConfigureAwait(false);
        }

        if (validation.IsFailure)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(validation.Failure!)
                .ConfigureAwait(false);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new ValidationFailure("The update operation was cancelled."))
                .ConfigureAwait(false);
        }
        catch (SaveChangesSecurityException exception)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(exception.Failure)
                .ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new ConcurrencyFailure<TKey>(
                    typeof(TEntity).Name,
                    key,
                    expectedToken))
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new PersistenceFailure("The update operation failed."))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new PersistenceFailure("The update operation failed."))
                .ConfigureAwait(false);
        }

        ConcurrencyToken newToken;
        try
        {
            newToken = _concurrencyTokenAccessor.Read(entity);
        }
        catch (ConcurrencyConfigurationException exception)
        {
            return await AbortAsync<EntityUpdateResult<TEntity>>(
                new ConcurrencyConfigurationFailure(exception.Message))
                .ConfigureAwait(false);
        }

        return DataResult.Succeeded(
            new EntityUpdateResult<TEntity>(entity, newToken));
    }

    private DataResult<T>? EnsureActiveTransaction<T>()
    {
        if (!_unitOfWork.HasActiveTransaction
            || _unitOfWork.CurrentTransaction?.State != DataTransactionState.Active
            || _context.Database.CurrentTransaction is null)
        {
            return DataResult.Failed<T>(
                new TransactionRequiredFailure("write"));
        }

        return null;
    }

    private async ValueTask<DataResult<TEntity>> LoadForUpdateAsync(
        TKey key,
        CancellationToken cancellationToken)
    {
        EntityEntry<TEntity>? tracked = FindTracked(key);
        if (tracked is not null && tracked.State != EntityState.Unchanged)
        {
            return DataResult.Failed<TEntity>(
                new ValidationFailure(
                    "The entity already has pending changes. Commit or discard them before updating."));
        }

        Expression<Func<TEntity, bool>> predicate = BuildKeyPredicate(key);
        TEntity? entity = await _context.Set<TEntity>()
            .AsNoTracking()
            .Where(predicate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            if (tracked is not null)
            {
                tracked.State = EntityState.Detached;
            }

            return DataResult.Failed<TEntity>(
                new NotFoundFailure<TKey>(typeof(TEntity).Name, key));
        }

        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        if (!TenantValueGuard.IsCanonical(entity.TenantId, typeof(TTenantId)))
        {
            return DataResult.Failed<TEntity>(
                new TenantOwnershipFailure(
                    "The persisted entity tenant is not a canonical string tenant identifier."));
        }

        _context.Attach(entity);
        return DataResult.Succeeded(entity);
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

    private void Detach(TEntity entity)
    {
        _context.Entry(entity).State = EntityState.Detached;
    }

    private EntityMutationGuard<TEntity> CaptureMutationGuard(TEntity entity)
    {
        return new EntityMutationGuard<TEntity>(
            entity,
            primaryKeySelector: item => _compiledKeySelector(item),
            tenantIdSelector: item => _tenantIdSelector(item),
            isDeletedSelector: _isSoftDeletable
                ? item => ((ISoftDeletable)item).IsDeleted
                : null,
            deletedAtUtcSelector: _isSoftDeletable
                ? item => ((ISoftDeletable)item).DeletedAtUtc
                : null,
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
