using System.Linq.Expressions;
using System.Reflection;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;

/// <summary>
/// Adds SQL Server exact string tenant equality to the standard tenant query filter.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server ignores trailing spaces in <c>nvarchar</c> equality comparisons even with
/// <c>_BIN2</c>. The augmenter adds:
/// </para>
/// <code>
/// LEN(TenantId + N'x') = LEN(@tenant + N'x')
/// </code>
/// <para>
/// Appending a non-space suffix makes trailing spaces internal, so <c>LEN</c> counts them. This is
/// exact for <c>nvarchar</c> without depending on parameter byte length.
/// The original equality predicate remains in place so indexes can be used.
/// </para>
/// <para>
/// String mappings must implement <see cref="ITenantExactMapping"/>; custom mappings that cannot
/// prove their filter is the direct tenant property equality fail closed. Guid tenant mappings are
/// returned unchanged.
/// </para>
/// </remarks>
public sealed class SqlServerTenantFilterExpressionAugmenter
    : ITenantFilterExpressionAugmenter
{
    private const string Suffix = "x";

    private static readonly MethodInfo TenantLengthMethod =
        typeof(SqlServerTenantFunctions).GetMethod(
            nameof(SqlServerTenantFunctions.TenantLength),
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "The SQL Server tenant length function could not be resolved.");

    private static readonly MethodInfo StringConcatMethod =
        typeof(string).GetMethod(
            nameof(string.Concat),
            [typeof(string), typeof(string)])
        ?? throw new InvalidOperationException(
            "The string concatenation method could not be resolved.");

    /// <summary>
    /// Gets the stateless augmenter instance.
    /// </summary>
    public static SqlServerTenantFilterExpressionAugmenter Instance { get; } = new();

    /// <summary>
    /// Initializes a SQL Server tenant filter augmenter.
    /// </summary>
    public SqlServerTenantFilterExpressionAugmenter()
    {
    }

    /// <inheritdoc />
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDbFunction(
            TenantLengthMethod,
            function =>
            {
                function.HasName("LEN");
                function.IsBuiltIn(true);
            });
    }

    /// <inheritdoc />
    public LambdaExpression Augment(
        MecesoftDbContext context,
        ITenantEntityMapping mapping,
        LambdaExpression tenantFilter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(tenantFilter);

        if (mapping.TenantIdType == typeof(object))
        {
            throw new TenantConfigurationException(
                $"The tenant mapping for '{mapping.EntityType.Name}' must declare a concrete "
                + "tenant identifier type for SQL Server exact equality.");
        }

        if (mapping.TenantIdType != typeof(string))
        {
            return tenantFilter;
        }

        if (mapping is not ITenantExactMapping)
        {
            throw new TenantConfigurationException(
                $"The string tenant mapping for '{mapping.EntityType.Name}' must implement "
                + $"{nameof(ITenantExactMapping)} so SQL Server exact equality can be applied.");
        }

        if (tenantFilter.ReturnType != typeof(bool)
            || tenantFilter.Parameters.Count != 1
            || tenantFilter.Parameters[0].Type != mapping.EntityType)
        {
            throw new TenantConfigurationException(
                $"The tenant filter for '{mapping.EntityType.Name}' has an unsupported shape.");
        }

        ParameterExpression entity = tenantFilter.Parameters[0];
        Expression entityTenant = Expression.Property(
            entity,
            mapping.TenantPropertyName);

        if (entityTenant.Type != typeof(string))
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{mapping.EntityType.Name}' must be a string.");
        }

        Expression currentTenant = Expression.Convert(
            Expression.Property(
                Expression.Constant(context),
                nameof(MecesoftDbContext.TenantId)),
            typeof(string));

        ConstantExpression suffix = Expression.Constant(
            Suffix,
            typeof(string));

        Expression entityLength = Expression.Call(
            TenantLengthMethod,
            Expression.Call(StringConcatMethod, entityTenant, suffix));
        Expression currentLength = Expression.Call(
            TenantLengthMethod,
            Expression.Call(StringConcatMethod, currentTenant, suffix));

        Expression exactEquality = Expression.AndAlso(
            Expression.NotEqual(
                entityTenant,
                Expression.Constant(null, typeof(string))),
            Expression.AndAlso(
                Expression.Equal(entityTenant, currentTenant),
                Expression.Equal(entityLength, currentLength)));

        return Expression.Lambda(
            Expression.AndAlso(tenantFilter.Body, exactEquality),
            tenantFilter.Parameters);
    }
}

internal static class SqlServerTenantFunctions
{
    internal static int TenantLength(string value)
    {
        throw new NotSupportedException(
            "TenantLength is translated to SQL Server LEN and must not be invoked in memory.");
    }
}
