using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Expressions;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Repositories;

/// <summary>
/// EF Core read repository for one tenant-owned entity type.
/// </summary>
/// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
/// <typeparam name="TEntity">The tenant entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// Every read method resolves the tenant scope through the coordinator before the query delegate is
/// executed. Tenant and soft-delete named filters remain active, all queries are no-tracking, and
/// projections are restricted to expressions that are safe to translate server-side.
/// </remarks>
public sealed class EfReadRepository<TContext, TEntity, TKey, TTenantId>
    : IReadRepository<TEntity, TKey>
    where TContext : MecesoftDbContext
    where TEntity : class, ITenantEntity<TTenantId>
    where TKey : notnull
    where TTenantId : notnull
{
    private readonly TContext _context;
    private readonly TenantScopeCoordinator<TEntity, TTenantId> _tenantScope;
    private readonly Expression<Func<TEntity, TKey>> _keySelector;

    /// <summary>
    /// Initializes a new read repository.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    /// <param name="tenantScope">The tenant resolver coordinator.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    public EfReadRepository(
        TContext context,
        TenantScopeCoordinator<TEntity, TTenantId> tenantScope,
        Expression<Func<TEntity, TKey>> keySelector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantScope);
        ArgumentNullException.ThrowIfNull(keySelector);

        KeySelectorValidator.Validate(context, keySelector);
        _context = context;
        _tenantScope = tenantScope;
        _keySelector = keySelector;
    }

    /// <inheritdoc />
    public ValueTask<DataResult<TEntity>> GetByIdAsync(
        TKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return GetByIdCoreAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<TResult>> GetByIdAsync<TResult>(
        TKey key,
        IProjection<TEntity, TResult> projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(projection);

        DataResult<bool> projectionValidation =
            QueryExpressionValidator.ValidateProjection(projection.Selector);
        if (projectionValidation.IsFailure)
        {
            return DataResult.Failed<TResult>(projectionValidation.Failure!);
        }

        Expression<Func<TEntity, bool>> predicate = BuildKeyPredicate(key);
        DataResult<List<TResult>> result = await ExecuteReadAsync(
            async token => await CreateFilteredQuery(predicate)
                .Select(projection.Selector)
                .Take(1)
                .ToListAsync(token)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DataResult.Failed<TResult>(result.Failure!);
        }

        return result.Value.Count == 0
            ? DataResult.Failed<TResult>(
                new NotFoundFailure<TKey>(typeof(TEntity).Name, key))
            : DataResult.Succeeded(result.Value[0]);
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<TEntity>> FirstOrDefaultAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<TEntity>(specificationValidation.Failure!);
        }

        DataResult<List<TEntity>> result = await ExecuteReadAsync(
            async token =>
            {
                IQueryable<TEntity> query = ApplyOrdering(
                    CreateFilteredQuery(specification.Criteria),
                    specification.Sorts,
                    appendKey: true);
                return await query.Take(1).ToListAsync(token).ConfigureAwait(false);
            },
            cancellationToken,
            ValidateLoadedTenants).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DataResult.Failed<TEntity>(result.Failure!);
        }

        return result.Value.Count == 0
            ? DataResult.Failed<TEntity>(new ReadNotFoundFailure(typeof(TEntity).Name))
            : DataResult.Succeeded(result.Value[0]);
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<TResult>> FirstOrDefaultAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<TResult>(specificationValidation.Failure!);
        }

        DataResult<bool> projectionValidation =
            QueryExpressionValidator.ValidateProjection(specification.Selector);
        if (projectionValidation.IsFailure)
        {
            return DataResult.Failed<TResult>(projectionValidation.Failure!);
        }

        DataResult<List<TResult>> result = await ExecuteReadAsync(
            async token =>
            {
                IQueryable<TEntity> query = ApplyOrdering(
                    CreateFilteredQuery(specification.Criteria),
                    specification.Sorts,
                    appendKey: true);
                return await query
                    .Select(specification.Selector)
                    .Take(1)
                    .ToListAsync(token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DataResult.Failed<TResult>(result.Failure!);
        }

        return result.Value.Count == 0
            ? DataResult.Failed<TResult>(new ReadNotFoundFailure(typeof(TEntity).Name))
            : DataResult.Succeeded(result.Value[0]);
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<IReadOnlyList<TEntity>>> ListAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<IReadOnlyList<TEntity>>(specificationValidation.Failure!);
        }

        DataResult<List<TEntity>> result = await ExecuteReadAsync(
            async token =>
            {
                IQueryable<TEntity> query = ApplyOrdering(
                    CreateFilteredQuery(specification.Criteria),
                    specification.Sorts,
                    appendKey: true);
                return await query.ToListAsync(token).ConfigureAwait(false);
            },
            cancellationToken,
            ValidateLoadedTenants).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DataResult.Failed<IReadOnlyList<TEntity>>(result.Failure!);
        }

        return DataResult.Succeeded<IReadOnlyList<TEntity>>(result.Value.AsReadOnly());
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<IReadOnlyList<TResult>>> ListAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<IReadOnlyList<TResult>>(specificationValidation.Failure!);
        }

        DataResult<bool> projectionValidation =
            QueryExpressionValidator.ValidateProjection(specification.Selector);
        if (projectionValidation.IsFailure)
        {
            return DataResult.Failed<IReadOnlyList<TResult>>(projectionValidation.Failure!);
        }

        DataResult<List<TResult>> result = await ExecuteReadAsync(
            async token =>
            {
                IQueryable<TEntity> query = ApplyOrdering(
                    CreateFilteredQuery(specification.Criteria),
                    specification.Sorts,
                    appendKey: true);
                return await query
                    .Select(specification.Selector)
                    .ToListAsync(token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? DataResult.Failed<IReadOnlyList<TResult>>(result.Failure!)
            : DataResult.Succeeded<IReadOnlyList<TResult>>(result.Value.AsReadOnly());
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<PageResult<TResult>>> PageAsync<TResult>(
        IProjectionSpecification<TEntity, TResult> specification,
        PageRequest pageRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(pageRequest);

        DataResult<bool> orderingValidation =
            SpecificationContractGuard.ValidateForPagination(specification);
        if (orderingValidation.IsFailure)
        {
            return DataResult.Failed<PageResult<TResult>>(orderingValidation.Failure!);
        }

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<PageResult<TResult>>(specificationValidation.Failure!);
        }

        DataResult<bool> projectionValidation =
            QueryExpressionValidator.ValidateProjection(specification.Selector);
        if (projectionValidation.IsFailure)
        {
            return DataResult.Failed<PageResult<TResult>>(projectionValidation.Failure!);
        }

        long offset = pageRequest.Offset;
        if (offset > int.MaxValue)
        {
            return DataResult.Failed<PageResult<TResult>>(
                new ValidationFailure("The page offset exceeds the supported range."));
        }

        DataResult<PageResult<TResult>> result = await ExecuteReadAsync(
            async token =>
            {
                IQueryable<TEntity> filtered = CreateFilteredQuery(specification.Criteria);
                long totalCount = await filtered
                    .LongCountAsync(token)
                    .ConfigureAwait(false);

                IQueryable<TEntity> ordered = ApplyOrdering(
                    filtered,
                    specification.Sorts,
                    appendKey: true);

                List<TResult> items = await ordered
                    .Select(specification.Selector)
                    .Skip((int)offset)
                    .Take(pageRequest.PageSize)
                    .ToListAsync(token)
                    .ConfigureAwait(false);

                return new PageResult<TResult>(
                    items,
                    pageRequest.PageNumber,
                    pageRequest.PageSize,
                    totalCount);
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<long>> CountAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<long>(specificationValidation.Failure!);
        }

        return await ExecuteReadAsync(
            async token => await CreateFilteredQuery(specification.Criteria)
                .LongCountAsync(token)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<bool>> AnyAsync(
        ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        DataResult<bool> specificationValidation = ValidateSpecification(specification);
        if (specificationValidation.IsFailure)
        {
            return DataResult.Failed<bool>(specificationValidation.Failure!);
        }

        return await ExecuteReadAsync(
            async token => await CreateFilteredQuery(specification.Criteria)
                .AnyAsync(token)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DataResult<TEntity>> GetByIdCoreAsync(
        TKey key,
        CancellationToken cancellationToken)
    {
        Expression<Func<TEntity, bool>> predicate = BuildKeyPredicate(key);
        DataResult<List<TEntity>> result = await ExecuteReadAsync(
            async token => await CreateFilteredQuery(predicate)
                .Take(1)
                .ToListAsync(token)
                .ConfigureAwait(false),
            cancellationToken,
            ValidateLoadedTenants).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return DataResult.Failed<TEntity>(result.Failure!);
        }

        return result.Value.Count == 0
            ? DataResult.Failed<TEntity>(
                new NotFoundFailure<TKey>(typeof(TEntity).Name, key))
            : DataResult.Succeeded(result.Value[0]);
    }

    private DataResult<bool> ValidateLoadedTenants(IReadOnlyList<TEntity> entities)
    {
        foreach (TEntity entity in entities)
        {
            if (!TenantValueGuard.IsCanonical(entity.TenantId, typeof(TTenantId)))
            {
                return DataResult.Failed<bool>(
                    new TenantOwnershipFailure(
                        "A persisted row contains a non-canonical string tenant identifier."));
            }

            if (!_context.IsReadAuthorizedFor(entity.TenantId!))
            {
                return DataResult.Failed<bool>(
                    new TenantOwnershipFailure(
                        "A materialized row does not match the resolved tenant read scope."));
            }
        }

        return DataResult.Succeeded(true);
    }

    private async ValueTask<DataResult<TResult>> ExecuteReadAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> query,
        CancellationToken cancellationToken,
        Func<TResult, DataResult<bool>>? validateResult = null)
    {
        DataResult<TenantScope<TTenantId>> scope = await _tenantScope
            .ResolveAsync(TenantOperation.Read, cancellationToken)
            .ConfigureAwait(false);

        if (scope.IsFailure)
        {
            return DataResult.Failed<TResult>(scope.Failure!);
        }

        try
        {
            _context.AuthorizeReadScope(
                scope.Value.TenantId,
                TenantOperation.Read);
        }
        catch
        {
            _context.ClearReadAuthorization();
            throw;
        }

        DataResult<bool> collation = _context.VerifyTenantCollation();
        if (collation.IsFailure)
        {
            _context.ClearReadAuthorization();
            return DataResult.Failed<TResult>(collation.Failure!);
        }

        try
        {
            TResult value = await query(cancellationToken).ConfigureAwait(false);
            if (validateResult is not null)
            {
                DataResult<bool> validation = validateResult(value);
                if (validation.IsFailure)
                {
                    return DataResult.Failed<TResult>(validation.Failure!);
                }
            }

            return DataResult.Succeeded(value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException)
        {
            return DataResult.Failed<TResult>(
                new PersistenceFailure("The read query failed."));
        }
        catch (InvalidOperationException)
        {
            return DataResult.Failed<TResult>(
                new UnsupportedExpressionFailure(
                    "query",
                    "The query expression could not be translated to SQL."));
        }
        catch (NotSupportedException)
        {
            return DataResult.Failed<TResult>(
                new UnsupportedExpressionFailure(
                    "query",
                    "The query expression is not supported by the adapter."));
        }
        catch (Exception)
        {
            return DataResult.Failed<TResult>(
                new PersistenceFailure("The read query failed."));
        }
        finally
        {
            _context.ClearReadAuthorization();
        }
    }

    private IQueryable<TEntity> CreateFilteredQuery(
        Expression<Func<TEntity, bool>>? criteria)
    {
        IQueryable<TEntity> query = _context.Set<TEntity>().AsNoTracking();
        return criteria is null ? query : query.Where(criteria);
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

    private IQueryable<TEntity> ApplyOrdering(
        IQueryable<TEntity> query,
        IReadOnlyList<ISortExpression<TEntity>> sorts,
        bool appendKey)
    {
        IOrderedQueryable<TEntity>? ordered = null;

        foreach (ISortExpression<TEntity> sort in sorts)
        {
            if (sort is null || sort.KeySelector is null || !Enum.IsDefined(sort.Direction))
            {
                throw new InvalidOperationException("A sort expression is invalid.");
            }

            string methodName = (ordered, sort.Direction) switch
            {
                (null, SortDirection.Ascending) => nameof(Queryable.OrderBy),
                (null, SortDirection.Descending) => nameof(Queryable.OrderByDescending),
                (_, SortDirection.Ascending) => nameof(Queryable.ThenBy),
                (_, SortDirection.Descending) => nameof(Queryable.ThenByDescending),
                _ => throw new InvalidOperationException("The sort direction is invalid."),
            };

            ordered = ApplyOrder(
                query,
                ordered,
                methodName,
                sort.KeySelector);
        }

        if (appendKey)
        {
            string methodName = ordered is null
                ? nameof(Queryable.OrderBy)
                : nameof(Queryable.ThenBy);

            ordered = ApplyOrder(query, ordered, methodName, _keySelector);
        }

        return ordered ?? query;
    }

    private static IOrderedQueryable<TEntity> ApplyOrder(
        IQueryable<TEntity> source,
        IOrderedQueryable<TEntity>? ordered,
        string methodName,
        LambdaExpression selector)
    {
        MethodInfo method = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == methodName
                && candidate.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(TEntity), selector.ReturnType);

        Expression sourceExpression = ordered is null
            ? source.Expression
            : ordered.Expression;
        Expression call = Expression.Call(
            method,
            sourceExpression,
            Expression.Quote(selector));

        return (IOrderedQueryable<TEntity>)source.Provider.CreateQuery<TEntity>(call);
    }

    private static DataResult<bool> ValidateSpecification(
        ISpecification<TEntity> specification)
    {
        if (specification.Sorts is null)
        {
            return DataResult.Failed<bool>(
                new UnsupportedExpressionFailure(
                    "sort",
                    "The sort collection is missing."));
        }

        foreach (ISortExpression<TEntity> sort in specification.Sorts)
        {
            if (sort is null
                || sort.KeySelector is null
                || !Enum.IsDefined(sort.Direction))
            {
                return DataResult.Failed<bool>(
                    new UnsupportedExpressionFailure(
                        "sort",
                        "A sort expression is missing or invalid."));
            }
        }

        return DataResult.Succeeded(true);
    }
}
