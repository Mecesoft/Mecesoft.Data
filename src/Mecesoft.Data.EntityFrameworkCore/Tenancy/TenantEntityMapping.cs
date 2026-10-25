using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Expressions;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Strongly typed tenant mapping for one entity type.
/// </summary>
/// <typeparam name="TEntity">The tenant entity type.</typeparam>
/// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
/// <remarks>
/// The selector must select a direct root-entity property. Nested selectors are rejected because
/// SaveChanges write validation requires the original and current property values from the entity entry.
/// </remarks>
public sealed class TenantEntityMapping<TEntity, TTenantId>
    : ITenantEntityMapping, ITenantExactMapping
    where TEntity : class, ITenantEntity<TTenantId>
    where TTenantId : notnull
{
    /// <summary>
    /// Initializes a new tenant mapping.
    /// </summary>
    /// <param name="tenantSelector">The entity tenant selector.</param>
    public TenantEntityMapping(Expression<Func<TEntity, TTenantId>> tenantSelector)
    {
        ArgumentNullException.ThrowIfNull(tenantSelector);

        DataResult<bool> validation = TenantContractGuard.ValidateSelector(tenantSelector);
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Failure!.Message, nameof(tenantSelector));
        }

        string? directPropertyName = GetDirectMemberName(tenantSelector.Body);
        if (directPropertyName is null)
        {
            throw new ArgumentException(
                "A tenant mapping must select a direct entity property; nested selectors are not supported for write validation.",
                nameof(tenantSelector));
        }

        TenantSelector = tenantSelector;
        _compiledTenantSelector = tenantSelector.Compile();
        TenantPropertyName = directPropertyName;
    }

    private readonly Func<TEntity, TTenantId> _compiledTenantSelector;

    /// <summary>
    /// Gets the entity tenant selector.
    /// </summary>
    public Expression<Func<TEntity, TTenantId>> TenantSelector { get; }

    /// <inheritdoc />
    public Type EntityType => typeof(TEntity);

    /// <inheritdoc />
    public Type TenantIdType => typeof(TTenantId);

    /// <inheritdoc />
    public string TenantPropertyName { get; }

    /// <inheritdoc />
    public object? GetTenantId(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _compiledTenantSelector((TEntity)entity);
    }

    /// <inheritdoc />
    public LambdaExpression BuildFilter(MecesoftDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "entity");
        Expression selectorBody = ReplaceParameter(
            TenantSelector.Body,
            TenantSelector.Parameters[0],
            parameter);
        Expression contextConstant = Expression.Constant(context);
        Expression tenantValue = Expression.Property(
            contextConstant,
            nameof(MecesoftDbContext.TenantId));

        Type comparableType = typeof(TTenantId).IsValueType
            && Nullable.GetUnderlyingType(typeof(TTenantId)) is null
                ? typeof(Nullable<>).MakeGenericType(typeof(TTenantId))
                : typeof(TTenantId);

        Expression entityTenant = selectorBody.Type == comparableType
            ? selectorBody
            : Expression.Convert(selectorBody, comparableType);
        Expression currentTenant = Expression.Convert(tenantValue, comparableType);
        Expression hasTenant = Expression.NotEqual(
            tenantValue,
            Expression.Constant(null));

        return Expression.Lambda(
            Expression.AndAlso(hasTenant, Expression.Equal(entityTenant, currentTenant)),
            parameter);
    }

    private static Expression ReplaceParameter(
        Expression expression,
        ParameterExpression source,
        ParameterExpression target)
    {
        var visitor = new ExpressionParameterReplacer(source, target);
        return visitor.Visit(expression);
    }

    private static string? GetDirectMemberName(Expression expression)
    {
        while (expression is UnaryExpression unary
            && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = unary.Operand;
        }

        return expression is MemberExpression member
            && member.Expression is ParameterExpression
                ? member.Member.Name
                : null;
    }
}
