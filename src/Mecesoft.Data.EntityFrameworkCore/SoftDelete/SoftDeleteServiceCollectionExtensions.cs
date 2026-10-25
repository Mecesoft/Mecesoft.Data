using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Dependency-injection helpers for the optional non-tenant soft-delete service.
/// </summary>
public static class SoftDeleteServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped soft-delete service for a standard DbContext.
    /// </summary>
    /// <typeparam name="TContext">The standard DbContext type.</typeparam>
    /// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
    /// <typeparam name="TKey">The entity key type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="keySelector">The single-column primary-key selector.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// The context must already be registered. A registered <see cref="TimeProvider"/> is used for
    /// <c>DeletedAtUtc</c>; otherwise <see cref="TimeProvider.System"/> is used. This service refuses
    /// to run against <c>MecesoftDbContext</c>.
    /// </remarks>
    public static IServiceCollection AddMecesoftSoftDeleteService<TContext, TEntity, TKey>(
        this IServiceCollection services,
        Expression<Func<TEntity, TKey>> keySelector)
        where TContext : DbContext
        where TEntity : class, ISoftDeletable
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.TryAddScoped<ISoftDeleteService<TEntity, TKey>>(
            serviceProvider => new SoftDeleteService<TContext, TEntity, TKey>(
                serviceProvider.GetRequiredService<TContext>(),
                keySelector,
                serviceProvider.GetService<TimeProvider>()));
        return services;
    }
}
