using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;

/// <summary>
/// PostgreSQL-specific rules for exact string tenant identity verification.
/// </summary>
internal static class PostgreSqlTenantCollationRules
{
    private const int TypeModifierHeader = 4;

    /// <summary>
    /// Returns whether the PostgreSQL type is a supported string type.
    /// </summary>
    public static bool IsSupportedType(string? typeName)
    {
        return string.Equals(typeName, "text", StringComparison.Ordinal)
            || string.Equals(typeName, "varchar", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether the type modifier provides enough capacity for the maximum canonical tenant
    /// identifier.
    /// </summary>
    /// <param name="typeName">The PostgreSQL type name.</param>
    /// <param name="typeModifier">The <c>pg_attribute.atttypmod</c> value.</param>
    /// <returns>
    /// <see langword="true"/> for <c>text</c>, unlimited <c>varchar</c>, or <c>varchar(n)</c> where
    /// <c>n</c> is at least <see cref="TenantIdValidation.MaxStringLength"/>.
    /// </returns>
    public static bool IsSupportedCapacity(string? typeName, int typeModifier)
    {
        if (string.Equals(typeName, "text", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(typeName, "varchar", StringComparison.Ordinal))
        {
            return false;
        }

        return typeModifier == -1
            || typeModifier >= TypeModifierHeader + TenantIdValidation.MaxStringLength;
    }

    /// <summary>
    /// Returns whether the relation kind is a regular or partitioned table.
    /// </summary>
    public static bool IsSupportedRelationKind(string? relationKind)
    {
        return string.Equals(relationKind, "r", StringComparison.Ordinal)
            || string.Equals(relationKind, "p", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether the column is not generated/computed.
    /// </summary>
    public static bool IsSupportedGeneration(string? generation)
    {
        return string.IsNullOrEmpty(generation);
    }

    /// <summary>
    /// Returns whether the collation is deterministic.
    /// </summary>
    public static bool IsSupportedDeterminism(bool? isDeterministic)
    {
        return isDeterministic == true;
    }
}
