using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;

/// <summary>
/// Configures SQL Server production tenant security on DbContext options.
/// </summary>
public static class SqlServerTenantOptionsBuilderExtensions
{
    /// <summary>
    /// Enables the trusted production verifier and exact tenant equality together.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    /// <remarks>
    /// This is the recommended SQL Server string tenant configuration. It installs the production
    /// collation verifier and the exact tenant filter augmenter atomically. String tenant mappings
    /// without this configuration fail closed.
    /// </remarks>
    public static DbContextOptionsBuilder UseMecesoftSqlServerTenancy(
        this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder.UseMecesoftTenantSecurity(
            SqlServerTenantCollationVerifier.Instance,
            SqlServerTenantFilterExpressionAugmenter.Instance);

        return optionsBuilder;
    }

    /// <summary>
    /// Enables the trusted production verifier and exact tenant equality together.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext>
        UseMecesoftSqlServerTenancy<TContext>(
            this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        optionsBuilder.UseMecesoftTenantSecurity(
            SqlServerTenantCollationVerifier.Instance,
            SqlServerTenantFilterExpressionAugmenter.Instance);

        return optionsBuilder;
    }

    /// <summary>
    /// Compatibility alias for <see cref="UseMecesoftSqlServerTenancy(DbContextOptionsBuilder)"/>.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder UseMecesoftSqlServerExactTenancy(
        this DbContextOptionsBuilder optionsBuilder)
        => UseMecesoftSqlServerTenancy(optionsBuilder);

    /// <summary>
    /// Compatibility alias for <see cref="UseMecesoftSqlServerTenancy{TContext}"/>.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext>
        UseMecesoftSqlServerExactTenancy<TContext>(
            this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => UseMecesoftSqlServerTenancy(optionsBuilder);
}
