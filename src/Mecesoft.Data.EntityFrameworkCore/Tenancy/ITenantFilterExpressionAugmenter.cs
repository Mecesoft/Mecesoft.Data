using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Optionally augments the tenant named query filter for a provider.
/// </summary>
/// <remarks>
/// <para>
/// The augmenter is a trusted security component. It must return a filter that remains bound to the
/// supplied mapping and must fail closed when it cannot prove the provider's exact tenant equality
/// semantics. The core applies the augmenter to every tenant mapping when it is configured and
/// verifies that the direct tenant equality predicate is still present in the returned filter.
/// </para>
/// <para>
/// <see cref="ConfigureModel"/> runs during model creation, before tenant filters are installed. It
/// can register provider-specific database functions required by <see cref="Augment"/>.
/// </para>
/// </remarks>
public interface ITenantFilterExpressionAugmenter
{
    /// <summary>
    /// Configures provider-specific model features required by the augmenter.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    /// <summary>
    /// Augments one tenant filter for the provider's exact equality semantics.
    /// </summary>
    /// <param name="context">The context being modeled.</param>
    /// <param name="mapping">The tenant mapping.</param>
    /// <param name="tenantFilter">The original tenant filter.</param>
    /// <returns>The augmented filter with the same entity parameter type.</returns>
    /// <exception cref="TenantConfigurationException">
    /// The mapping cannot be augmented safely.
    /// </exception>
    LambdaExpression Augment(
        MecesoftDbContext context,
        ITenantEntityMapping mapping,
        LambdaExpression tenantFilter);
}
