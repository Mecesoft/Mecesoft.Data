using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Configures the optional tenant filter augmentation extension point.
/// </summary>
public static class TenantFilterOptionsBuilderExtensions
{
    /// <summary>
    /// Adds or replaces the tenant filter augmenter in the context options.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="augmenter">The trusted provider-specific augmenter.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder UseMecesoftTenantFilterAugmentation(
        this DbContextOptionsBuilder optionsBuilder,
        ITenantFilterExpressionAugmenter augmenter)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(augmenter);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(augmenter));

        return optionsBuilder;
    }

    /// <summary>
    /// Adds or replaces the tenant filter augmenter in the context options.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="augmenter">The trusted provider-specific augmenter.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext>
        UseMecesoftTenantFilterAugmentation<TContext>(
            this DbContextOptionsBuilder<TContext> optionsBuilder,
            ITenantFilterExpressionAugmenter augmenter)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(augmenter);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(augmenter));

        return optionsBuilder;
    }

    /// <summary>
    /// Configures mandatory tenant security: a trusted verifier and an exact filter augmenter.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="verifier">The trusted production verifier.</param>
    /// <param name="augmenter">The provider exactness augmenter.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder UseMecesoftTenantSecurity(
        this DbContextOptionsBuilder optionsBuilder,
        ITenantCollationVerifier verifier,
        ITenantFilterExpressionAugmenter augmenter)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(augmenter);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(
                    augmenter,
                    verifier,
                    requireTrustedVerifier: true,
                    requireExactTenantFilters: true));

        return optionsBuilder;
    }

    /// <summary>
    /// Configures mandatory tenant security: a trusted verifier and an exact filter augmenter.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="verifier">The trusted production verifier.</param>
    /// <param name="augmenter">The provider exactness augmenter.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseMecesoftTenantSecurity<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        ITenantCollationVerifier verifier,
        ITenantFilterExpressionAugmenter augmenter)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(augmenter);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(
                    augmenter,
                    verifier,
                    requireTrustedVerifier: true,
                    requireExactTenantFilters: true));

        return optionsBuilder;
    }

    /// <summary>
    /// Configures mandatory tenant collation verification without a filter augmenter.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="verifier">The trusted production verifier.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder UseMecesoftTenantVerification(
        this DbContextOptionsBuilder optionsBuilder,
        ITenantCollationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(verifier);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(
                    augmenter: null,
                    verifier,
                    requireTrustedVerifier: true,
                    requireExactTenantFilters: false));

        return optionsBuilder;
    }

    /// <summary>
    /// Configures mandatory tenant collation verification without a filter augmenter.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="verifier">The trusted production verifier.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext> UseMecesoftTenantVerification<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        ITenantCollationVerifier verifier)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(verifier);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(
                new TenantFilterAugmentationOptionsExtension(
                    augmenter: null,
                    verifier,
                    requireTrustedVerifier: true,
                    requireExactTenantFilters: false));

        return optionsBuilder;
    }
}
