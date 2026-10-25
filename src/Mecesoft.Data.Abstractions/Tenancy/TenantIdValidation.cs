namespace Mecesoft.Data.Abstractions.Tenancy;

/// <summary>
/// Defines the canonical form required for string tenant identifiers.
/// </summary>
/// <remarks>
/// <para>
/// A canonical string tenant identifier is non-empty, contains at most
/// <see cref="MaxStringLength"/> UTF-16 code units, and does not start or end with Unicode
/// whitespace. Validation is ordinal and culture-independent; it does not trim, normalize, or
/// otherwise change the identifier.
/// </para>
/// <para>
/// Interior whitespace is allowed because it can be part of a legitimate tenant name. Guid tenant
/// identifiers are not subject to these string rules.
/// </para>
/// </remarks>
public static class TenantIdValidation
{
    /// <summary>
    /// Gets the maximum allowed length of a string tenant identifier, in UTF-16 code units.
    /// </summary>
    public const int MaxStringLength = 128;

    /// <summary>
    /// Returns whether a string tenant identifier is canonical.
    /// </summary>
    /// <param name="tenantId">The tenant identifier to validate.</param>
    /// <returns>
    /// <see langword="true"/> when the identifier is non-empty, within the maximum length, and has
    /// no leading or trailing Unicode whitespace; otherwise <see langword="false"/>.
    /// </returns>
    public static bool IsCanonical(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId) || tenantId.Length > MaxStringLength)
        {
            return false;
        }

        return !char.IsWhiteSpace(tenantId[0])
            && !char.IsWhiteSpace(tenantId[tenantId.Length - 1]);
    }
}
