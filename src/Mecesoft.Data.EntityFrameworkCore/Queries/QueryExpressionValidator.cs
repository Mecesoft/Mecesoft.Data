using System.Linq.Expressions;
using System.Reflection;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore.Queries;

internal static class QueryExpressionValidator
{
    private static readonly HashSet<string> AllowedStringMethods =
    [
        nameof(string.Contains),
        nameof(string.StartsWith),
        nameof(string.EndsWith),
        nameof(string.ToLower),
        nameof(string.ToLowerInvariant),
        nameof(string.ToUpper),
        nameof(string.ToUpperInvariant),
        nameof(string.Trim),
        nameof(string.TrimStart),
        nameof(string.TrimEnd),
        nameof(string.Substring),
        nameof(string.Replace),
        nameof(string.IndexOf),
        nameof(string.LastIndexOf),
        nameof(string.Equals),
    ];

    public static DataResult<bool> ValidateProjection(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var visitor = new ProjectionVisitor();
        visitor.Visit(selector.Body);

        return visitor.Unsupported is null
            ? DataResult.Succeeded(true)
            : DataResult.Failed<bool>(
                new UnsupportedExpressionFailure("projection", visitor.Unsupported));
    }

    private static bool IsAllowedMethod(MethodInfo method)
    {
        Type? declaringType = method.DeclaringType;

        if (declaringType == typeof(string))
        {
            return AllowedStringMethods.Contains(method.Name);
        }

        if (declaringType == typeof(Math))
        {
            return true;
        }

        if (declaringType == typeof(DateTime) || declaringType == typeof(DateTimeOffset))
        {
            return !method.IsStatic;
        }

        if (declaringType == typeof(Nullable))
        {
            return true;
        }

        if (declaringType == typeof(Microsoft.EntityFrameworkCore.EF)
            || declaringType?.Namespace?.StartsWith(
                "Microsoft.EntityFrameworkCore",
                StringComparison.Ordinal) == true)
        {
            return true;
        }

        return false;
    }

    private static bool IsNondeterministicStaticMember(MemberInfo member)
    {
        Type? declaringType = member.DeclaringType;

        if (declaringType == typeof(DateTime)
            && member.Name is nameof(DateTime.Now) or nameof(DateTime.UtcNow) or nameof(DateTime.Today))
        {
            return true;
        }

        if (declaringType == typeof(DateTimeOffset)
            && member.Name is nameof(DateTimeOffset.Now) or nameof(DateTimeOffset.UtcNow))
        {
            return true;
        }

        return declaringType == typeof(Random);
    }

    private sealed class ProjectionVisitor : ExpressionVisitor
    {
        public string? Unsupported { get; private set; }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Unsupported ??= "Invocation expressions are not supported.";
            return base.VisitInvocation(node);
        }

        protected override Expression VisitBlock(BlockExpression node)
        {
            Unsupported ??= "Block expressions are not supported.";
            return base.VisitBlock(node);
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            Unsupported ??= "Nested lambda expressions are not supported in projections.";
            return base.VisitLambda(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!IsAllowedMethod(node.Method))
            {
                Unsupported ??= $"The method '{node.Method.Name}' is not supported in a server-side projection.";
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (IsNondeterministicStaticMember(node.Member))
            {
                Unsupported ??= $"The member '{node.Member.Name}' is not supported in a server-side projection.";
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (node.Type == typeof(Random))
            {
                Unsupported ??= "Random construction is not supported in a server-side projection.";
            }

            return base.VisitNew(node);
        }
    }
}
