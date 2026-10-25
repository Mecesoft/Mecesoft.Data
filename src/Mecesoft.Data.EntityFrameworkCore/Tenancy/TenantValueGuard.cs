using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Applies the canonical string tenant identifier rules to values read from the model or database.
/// </summary>
internal static class TenantValueGuard
{
    /// <summary>
    /// Returns whether the value is canonical for the supplied tenant identifier CLR type.
    /// </summary>
    /// <param name="value">The tenant identifier value.</param>
    /// <param name="tenantIdType">The actual mapped tenant property CLR type.</param>
    /// <returns>
    /// For string tenant types, whether the value is canonical. For non-string types, a string value
    /// is still validated defensively; other values are accepted. A missing string value is not
    /// canonical.
    /// </returns>
    public static bool IsCanonical(object? value, Type tenantIdType)
    {
        ArgumentNullException.ThrowIfNull(tenantIdType);

        if (tenantIdType == typeof(string))
        {
            return value is string text && TenantIdValidation.IsCanonical(text);
        }

        return value is not string nonStringValue
            || TenantIdValidation.IsCanonical(nonStringValue);
    }
}
