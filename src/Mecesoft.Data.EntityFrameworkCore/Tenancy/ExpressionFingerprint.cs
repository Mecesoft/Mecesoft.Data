using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Builds a deterministic structural fingerprint for mapping expressions.
/// </summary>
/// <remarks>
/// The fingerprint is used only for model-cache identity and consistency checks. It intentionally
/// rejects unsupported nodes and captured closure state instead of falling back to
/// <see cref="object.ToString"/> or an object reference, both of which are not reliable security
/// identities. A mapping that cannot be fingerprinted fails closed during context construction.
/// </remarks>
internal static class ExpressionFingerprint
{
    public static string Create(LambdaExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Visit(expression, builder);
        return builder.ToString();
    }

    public static bool AreEqual(
        LambdaExpression left,
        LambdaExpression right)
    {
        return string.Equals(
            Create(left),
            Create(right),
            StringComparison.Ordinal);
    }

    private static void Visit(Expression expression, StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(expression);

        AppendToken(builder, expression.NodeType.ToString());
        AppendType(builder, expression.Type);

        switch (expression)
        {
            case LambdaExpression lambda:
                AppendToken(builder, "lambda");
                AppendType(builder, lambda.ReturnType);
                AppendInt(builder, lambda.Parameters.Count);
                foreach (ParameterExpression parameter in lambda.Parameters)
                {
                    AppendParameter(builder, parameter);
                }

                Visit(lambda.Body, builder);
                break;

            case ParameterExpression parameter:
                AppendToken(builder, "parameter");
                AppendParameter(builder, parameter);
                break;

            case MemberExpression member:
                AppendMember(builder, member);
                break;

            case ConstantExpression constant:
                AppendToken(builder, "constant");
                AppendConstant(builder, constant.Value);
                break;

            case BinaryExpression binary:
                AppendToken(builder, "binary");
                AppendToken(builder, binary.NodeType.ToString());
                if (binary.Method is not null)
                {
                    AppendMethod(builder, binary.Method);
                }

                Visit(binary.Left, builder);
                Visit(binary.Right, builder);
                if (binary.Conversion is not null)
                {
                    Visit(binary.Conversion, builder);
                }

                break;

            case UnaryExpression unary:
                AppendToken(builder, "unary");
                AppendToken(builder, unary.NodeType.ToString());
                if (unary.Method is not null)
                {
                    AppendMethod(builder, unary.Method);
                }

                Visit(unary.Operand, builder);
                break;

            case MethodCallExpression call:
                AppendToken(builder, "call");
                AppendMethod(builder, call.Method);
                if (call.Object is not null)
                {
                    Visit(call.Object, builder);
                }
                else
                {
                    throw new TenantConfigurationException(
                        "The soft-delete visibility expression cannot depend on static method state.");
                }

                AppendInt(builder, call.Arguments.Count);
                foreach (Expression argument in call.Arguments)
                {
                    Visit(argument, builder);
                }

                break;

            case ConditionalExpression conditional:
                AppendToken(builder, "conditional");
                Visit(conditional.Test, builder);
                Visit(conditional.IfTrue, builder);
                Visit(conditional.IfFalse, builder);
                break;

            case TypeBinaryExpression typeBinary:
                AppendToken(builder, "typebinary");
                AppendType(builder, typeBinary.TypeOperand);
                Visit(typeBinary.Expression, builder);
                break;

            case NewExpression newExpression:
                AppendToken(builder, "new");
                AppendMethod(builder, newExpression.Constructor);
                AppendInt(builder, newExpression.Arguments.Count);
                foreach (Expression argument in newExpression.Arguments)
                {
                    Visit(argument, builder);
                }

                break;

            case NewArrayExpression newArray:
                AppendToken(builder, "newarray");
                AppendInt(builder, newArray.Expressions.Count);
                foreach (Expression item in newArray.Expressions)
                {
                    Visit(item, builder);
                }

                break;

            case DefaultExpression:
                AppendToken(builder, "default");
                break;

            case IndexExpression index:
                AppendToken(builder, "index");
                if (index.Indexer is not null)
                {
                    AppendToken(builder, index.Indexer.Name);
                }

                if (index.Object is not null)
                {
                    Visit(index.Object, builder);
                }
                else
                {
                    AppendToken(builder, "static");
                }

                AppendInt(builder, index.Arguments.Count);
                foreach (Expression argument in index.Arguments)
                {
                    Visit(argument, builder);
                }

                break;

            case InvocationExpression invocation:
                AppendToken(builder, "invoke");
                Visit(invocation.Expression, builder);
                AppendInt(builder, invocation.Arguments.Count);
                foreach (Expression argument in invocation.Arguments)
                {
                    Visit(argument, builder);
                }

                break;

            default:
                throw new TenantConfigurationException(
                    $"The soft-delete visibility expression contains an unsupported node "
                    + $"'{expression.NodeType}'.");
        }
    }

    private static void AppendMember(StringBuilder builder, MemberExpression member)
    {
        AppendToken(builder, "member");
        AppendType(builder, member.Member.DeclaringType);
        AppendToken(builder, member.Member.Name);
        AppendToken(builder, member.Member.MemberType.ToString());

        if (member.Expression is null)
        {
            throw new TenantConfigurationException(
                "The soft-delete visibility expression cannot depend on static member state.");
        }

        if (member.Expression is ConstantExpression constant
            && constant.Value is not null)
        {
            throw new TenantConfigurationException(
                "The soft-delete visibility expression cannot capture closure state.");
        }

        Visit(member.Expression, builder);
    }

    private static void AppendConstant(StringBuilder builder, object? value)
    {
        if (value is null)
        {
            AppendToken(builder, "null");
            return;
        }

        Type type = value.GetType();
        AppendType(builder, type);

        switch (value)
        {
            case string text:
                AppendToken(builder, text);
                break;
            case char character:
                AppendToken(builder, character.ToString());
                break;
            case bool boolean:
                AppendToken(builder, boolean ? "true" : "false");
                break;
            case byte number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case sbyte number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case short number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ushort number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case int number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case uint number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case ulong number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case float number:
                AppendToken(builder, number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case double number:
                AppendToken(builder, number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal number:
                AppendToken(builder, number.ToString(CultureInfo.InvariantCulture));
                break;
            case Enum enumeration:
                AppendToken(builder, enumeration.GetType().FullName ?? string.Empty);
                AppendToken(builder, Convert.ToInt64(
                    enumeration,
                    CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case Guid guid:
                AppendToken(builder, guid.ToString("D"));
                break;
            case DateTime dateTime:
                AppendToken(builder, dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                AppendToken(builder, dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                AppendToken(builder, timeSpan.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Type typeValue:
                AppendType(builder, typeValue);
                break;
            case byte[] bytes:
                AppendToken(builder, Convert.ToHexString(bytes));
                break;
            default:
                throw new TenantConfigurationException(
                    $"The soft-delete visibility expression contains an unsupported constant "
                    + $"of type '{type.FullName}'.");
        }
    }

    private static void AppendParameter(
        StringBuilder builder,
        ParameterExpression parameter)
    {
        AppendType(builder, parameter.Type);
        AppendToken(builder, parameter.Name ?? string.Empty);
    }

    private static void AppendMethod(
        StringBuilder builder,
        System.Reflection.MethodBase? method)
    {
        if (method is null)
        {
            AppendToken(builder, "none");
            return;
        }

        AppendType(builder, method.DeclaringType);
        AppendToken(builder, method.Name);
        AppendType(
            builder,
            method is System.Reflection.MethodInfo methodInfo
                ? methodInfo.ReturnType
                : typeof(void));
        AppendInt(builder, method.GetParameters().Length);
        Type[] genericArguments = method.IsGenericMethod
            ? method.GetGenericArguments()
            : Type.EmptyTypes;
        AppendInt(builder, genericArguments.Length);
        foreach (Type genericArgument in genericArguments)
        {
            AppendType(builder, genericArgument);
        }
    }

    private static void AppendType(StringBuilder builder, Type? type)
    {
        AppendToken(
            builder,
            type?.AssemblyQualifiedName
            ?? type?.FullName
            ?? string.Empty);
    }

    private static void AppendToken(StringBuilder builder, string value)
    {
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static void AppendInt(StringBuilder builder, int value)
    {
        builder.Append(value).Append('|');
    }
}
