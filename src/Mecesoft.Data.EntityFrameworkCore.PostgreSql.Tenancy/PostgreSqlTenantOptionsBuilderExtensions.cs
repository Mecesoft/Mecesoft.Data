using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;

/// <summary>
/// Configures PostgreSQL production tenant security on DbContext options.
/// </summary>
public static class PostgreSqlTenantOptionsBuilderExtensions
{
    /// <summary>
    /// Enables the trusted PostgreSQL production tenant collation verifier.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    /// <remarks>
    /// Call this after the PostgreSQL provider is configured. String tenant mappings without this
    /// configuration fail closed.
    /// </remarks>
    public static DbContextOptionsBuilder UseMecesoftPostgreSqlTenancy(
        this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder.UseMecesoftTenantVerification(
            PostgreSqlTenantCollationVerifier.Instance);

        return optionsBuilder;
    }

    /// <summary>
    /// Enables the trusted PostgreSQL production tenant collation verifier.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext>
        UseMecesoftPostgreSqlTenancy<TContext>(
            this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder.UseMecesoftTenantVerification(
            PostgreSqlTenantCollationVerifier.Instance);

        return optionsBuilder;
    }
}
