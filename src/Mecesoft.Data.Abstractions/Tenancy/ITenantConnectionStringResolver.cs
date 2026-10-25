namespace Mecesoft.Data.Abstractions.Tenancy;

/// <summary>
/// Resolves the database connection string for the current database-per-tenant scope.
/// </summary>
/// <remarks>
/// <para>
/// Database-per-tenant applications do not need the row-level tenant filters installed by
/// <c>MecesoftDbContext</c>. This contract lets an application build a standard
/// <c>DbContext</c> against the connection string that belongs to the resolved tenant. The
/// implementation must fail closed when no tenant is resolved; it must never return a shared
/// fallback connection for an unresolved tenant.
/// </para>
/// <para>
/// The resolver is scoped. Caching a tenant connection string in a singleton can leak one tenant's
/// database into another tenant's request.
/// </para>
/// </remarks>
public interface ITenantConnectionStringResolver
{
    /// <summary>
    /// Resolves the connection string for the current tenant scope.
    /// </summary>
    /// <returns>The tenant-scoped connection string.</returns>
    /// <exception cref="InvalidOperationException">
    /// No tenant is resolved or the resolved tenant does not have a connection string.
    /// </exception>
    string ResolveConnectionString();
}
