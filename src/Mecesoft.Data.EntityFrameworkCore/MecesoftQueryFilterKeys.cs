namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Defines the stable EF Core named query-filter keys used by Mecesoft.Data.
/// </summary>
public static class MecesoftQueryFilterKeys
{
    /// <summary>
    /// The mandatory tenant ownership filter key.
    /// </summary>
    public const string Tenant = "Mecesoft.Tenant";

    /// <summary>
    /// The soft-delete visibility filter key.
    /// </summary>
    public const string SoftDelete = "Mecesoft.SoftDelete";
}
