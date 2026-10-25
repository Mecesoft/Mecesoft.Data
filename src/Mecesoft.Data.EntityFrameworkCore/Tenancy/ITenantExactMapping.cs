namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Marks a tenant mapping whose filter is the standard direct tenant-property equality predicate.
/// </summary>
/// <remarks>
/// Provider-specific exactness augmenters may require this marker before appending an exact
/// comparison. Custom mappings must not implement this interface unless their filter is equivalent
/// to the direct tenant property equality predicate and their <c>GetTenantId</c> implementation
/// returns the same property value. The marker alone is not a security proof: the adapter validates
/// the tenant contract property and the filter shape independently.
/// </remarks>
public interface ITenantExactMapping
{
}
