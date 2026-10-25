using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.Repositories;

internal static class KeySelectorValidator
{
    public static void Validate<TEntity, TKey>(
        DbContext context,
        Expression<Func<TEntity, TKey>> keySelector)
        where TEntity : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keySelector);

        IEntityType entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new ArgumentException(
                "The entity type is not part of the DbContext model.",
                nameof(keySelector));

        IKey? primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1)
        {
            throw new ArgumentException(
                "The entity must have a single-column primary key.",
                nameof(keySelector));
        }

        IProperty primaryKeyProperty = primaryKey.Properties[0];
        Expression body = keySelector.Body;
        while (body is UnaryExpression unary
            && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Expression is not ParameterExpression parameter
            || parameter != keySelector.Parameters[0]
            || !string.Equals(
                member.Member.Name,
                primaryKeyProperty.Name,
                StringComparison.Ordinal)
            || keySelector.ReturnType != primaryKeyProperty.ClrType)
        {
            throw new ArgumentException(
                "The key selector must select the entity's primary key directly.",
                nameof(keySelector));
        }
    }
}
