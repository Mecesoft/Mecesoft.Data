using System.Linq.Expressions;

namespace Mecesoft.Data.EntityFrameworkCore.Expressions;

internal sealed class ExpressionParameterReplacer(
    ParameterExpression source,
    ParameterExpression target) : ExpressionVisitor
{
    protected override Expression VisitParameter(ParameterExpression node)
        => node == source ? target : base.VisitParameter(node);
}
