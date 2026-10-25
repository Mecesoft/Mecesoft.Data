using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Tenancy;

/// <summary>
/// Provides fail-closed validation helpers for tenant configuration.
/// </summary>
/// <remarks>
/// These helpers validate structure only. They do not prove that a selector points at the intended
/// tenant column, that the resolver is registered, or that an adapter applies the resulting
/// predicate. Adapters must still fail closed at runtime.
/// </remarks>
public static class TenantContractGuard
{
    /// <summary>
    /// Validates that a tenant selector is present and selects a member chain from the entity parameter.
    /// </summary>
    /// <typeparam name="TEntity">The protected entity type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="selector">The selector to validate.</param>
    /// <returns>Success when valid; otherwise, a <see cref="TenantConfigurationFailure"/>.</returns>
    /// <remarks>
    /// Constant, static, parameter-only, constructed, conditional, and method-call selectors are
    /// rejected. A selector such as <c>entity =&gt; entity.TenantId</c> or
    /// <c>entity =&gt; entity.Tenant.Id</c> is accepted. This is a structural guard, not a proof that
    /// the selected member is the correct tenant column.
    /// </remarks>
    public static DataResult<bool> ValidateSelector<TEntity, TTenantId>(
        Expression<Func<TEntity, TTenantId>>? selector)
        where TEntity : class, ITenantEntity<TTenantId>
        where TTenantId : notnull
    {
        if (selector is null)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure("A tenant selector is required."));
        }

        if (!IsMemberChainFromParameter(UnwrapConversions(selector.Body)))
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure(
                    "The tenant selector must select a member chain from the entity parameter."));
        }

        return DataResult.Succeeded(true);
    }

    private static Expression UnwrapConversions(Expression expression)
    {
        while (expression is UnaryExpression unary
            && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static bool IsMemberChainFromParameter(Expression expression)
    {
        expression = UnwrapConversions(expression);
        if (expression is not MemberExpression)
        {
            return false;
        }

        Expression? current = expression;
        while (current is MemberExpression member)
        {
            if (member.Expression is null)
            {
                return false;
            }

            Expression next = UnwrapConversions(member.Expression);
            if (next is ParameterExpression)
            {
                return true;
            }

            current = next;
        }

        return false;
    }
}
