using System.Linq.Expressions;

namespace Mecesoft.Data.Abstractions.SoftDelete;

/// <summary>
/// Composes a tenant predicate with a soft-delete visibility predicate.
/// </summary>
/// <remarks>
/// <para>
/// This helper is a cooperative contract helper, not a runtime enforcement mechanism. Adapters must
/// apply the returned predicate to every soft-delete and restore query. It is the supported way to
/// combine tenant ownership and soft-delete visibility without disabling the tenant filter.
/// </para>
/// <para>
/// An adapter must not call <c>IgnoreQueryFilters</c> and then query without re-applying the tenant
/// predicate. For restore, pass <c>requireDeleted: true</c> so the query can see the deleted row
/// while still requiring the tenant identifier to match.
/// </para>
/// </remarks>
public static class SoftDeleteQueryPolicy
{
    /// <summary>
    /// Builds a predicate that requires the tenant identifier to match and applies the requested
    /// soft-delete visibility.
    /// </summary>
    /// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="tenantSelector">The tenant identifier selector.</param>
    /// <param name="tenantId">The resolved tenant identifier. Callers must not supply an untrusted tenant identifier.</param>
    /// <param name="isDeletedPredicate">The predicate that identifies deleted rows.</param>
    /// <param name="requireDeleted">
    /// <see langword="true"/> to require a deleted row, as required by restore;
    /// <see langword="false"/> to require an active row.
    /// </param>
    /// <returns>A composable predicate containing both the tenant and soft-delete conditions.</returns>
    public static Expression<Func<TEntity, bool>> Compose<TEntity, TTenantId>(
        Expression<Func<TEntity, TTenantId>> tenantSelector,
        TTenantId tenantId,
        Expression<Func<TEntity, bool>> isDeletedPredicate,
        bool requireDeleted)
        where TEntity : class, ISoftDeletable
        where TTenantId : notnull
    {
        ArgumentNullException.ThrowIfNull(tenantSelector);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(isDeletedPredicate);

        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "entity");
        Expression tenantBody = ReplaceParameter(tenantSelector.Body, tenantSelector.Parameters[0], parameter);
        Expression deletedBody = ReplaceParameter(isDeletedPredicate.Body, isDeletedPredicate.Parameters[0], parameter);

        Expression tenantCondition = Expression.Equal(
            tenantBody,
            Expression.Constant(tenantId, typeof(TTenantId)));

        Expression visibilityCondition = requireDeleted
            ? deletedBody
            : Expression.Not(deletedBody);

        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.AndAlso(tenantCondition, visibilityCondition),
            parameter);
    }

    private static Expression ReplaceParameter(
        Expression expression,
        ParameterExpression source,
        ParameterExpression target)
    {
        var visitor = new ParameterReplacer(source, target);
        return visitor.Visit(expression);
    }

    private sealed class ParameterReplacer(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : base.VisitParameter(node);
    }
}
