using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;

/// <summary>
/// Dependency-injection registration for SQL Server tenant collation verification.
/// </summary>
public static class SqlServerTenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server tenant collation verifier as a singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// The application context must still expose the injected verifier through
    /// <c>MecesoftDbContext.TenantCollationVerifier</c>. Registration does not modify context
    /// behavior automatically.
    /// </remarks>
    public static IServiceCollection AddMecesoftSqlServerTenantCollationVerification(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITenantCollationVerifier>(
            SqlServerTenantCollationVerifier.Instance);
        return services;
    }
}
