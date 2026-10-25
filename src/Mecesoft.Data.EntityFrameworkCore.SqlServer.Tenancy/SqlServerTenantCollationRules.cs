namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;

/// <summary>
/// SQL Server-specific rules for exact string tenant identity verification.
/// </summary>
internal static class SqlServerTenantCollationRules
{
    /// <summary>
    /// Returns whether the collation provides exact binary equality.
    /// </summary>
    /// <remarks>
    /// Only <c>_BIN2</c> and <c>_BIN2_UTF8</c> are accepted. Linguistic case-sensitive
    /// collations are intentionally rejected because they can be accent-, width-, or
    /// kana-insensitive depending on their suffixes.
    /// </remarks>
    public static bool IsSupportedBin2Collation(string? collation)
    {
        if (string.IsNullOrWhiteSpace(collation))
        {
            return false;
        }

        return collation.EndsWith("_BIN2", StringComparison.OrdinalIgnoreCase)
            || collation.EndsWith("_BIN2_UTF8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether the SQL Server system type is an exact, lossless string type.
    /// </summary>
    /// <param name="typeName">The <c>sys.types.name</c> value.</param>
    /// <param name="systemTypeId">The <c>sys.columns.system_type_id</c> value.</param>
    /// <param name="isUserDefined">Whether the type is user-defined.</param>
    /// <returns><see langword="true"/> for non-user-defined <c>nvarchar</c> only.</returns>
    /// <remarks>
    /// <c>varchar</c> is intentionally rejected in every collation combination. EF Core/SqlClient
    /// sends string parameters as <c>varchar</c> when the mapped column is <c>varchar</c>, and SQL
    /// Server can lose Unicode tenant identifiers during that conversion, even with a UTF8
    /// collation. <c>nvarchar</c> is the only lossless option.
    /// </remarks>
    public static bool IsSupportedStringType(
        string? typeName,
        int systemTypeId,
        bool isUserDefined)
    {
        return !isUserDefined
            && systemTypeId == 231
            && string.Equals(typeName, "nvarchar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether the SQL Server string type and collation pair preserves all Unicode tenant
    /// identifiers without lossy code-page conversion.
    /// </summary>
    /// <param name="typeName">The <c>sys.types.name</c> value.</param>
    /// <param name="collation">The effective column collation.</param>
    /// <returns>
    /// <see langword="true"/> for <c>nvarchar</c> with <c>_BIN2</c> only; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsSupportedEncoding(string? typeName, string? collation)
    {
        return string.Equals(typeName, "nvarchar", StringComparison.OrdinalIgnoreCase)
            && IsSupportedBin2Collation(collation)
            && !IsUtf8Bin2Collation(collation);
    }

    /// <summary>
    /// Returns whether the column can store the maximum canonical string tenant identifier.
    /// </summary>
    /// <param name="typeName">The <c>sys.types.name</c> value.</param>
    /// <param name="maxLength">
    /// The <c>sys.columns.max_length</c> value in bytes, or <c>-1</c> for MAX.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when an <c>nvarchar</c> column has at least 256 bytes or is MAX;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool HasSufficientCapacity(string? typeName, int maxLength)
    {
        if (!string.Equals(typeName, "nvarchar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return maxLength == -1 || maxLength >= 256;
    }

    /// <summary>
    /// Returns whether the collation is a UTF8 <c>_BIN2</c> collation.
    /// </summary>
    public static bool IsUtf8Bin2Collation(string? collation)
    {
        return !string.IsNullOrWhiteSpace(collation)
            && collation.EndsWith("_BIN2_UTF8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns whether the SQL Server object type is a user table.
    /// </summary>
    /// <param name="objectType">The <c>sys.objects.type</c> value.</param>
    /// <returns><see langword="true"/> only for <c>U</c>.</returns>
    public static bool IsSupportedObjectType(string? objectType)
    {
        return string.Equals(objectType, "U", StringComparison.Ordinal);
    }
}
