using Mecesoft.Data.Abstractions.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mecesoft.Repository;

/// <summary>
/// Dependency-injection registration helpers for the compatibility and general-purpose APIs.
/// </summary>
public static class RepositoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped legacy-compatible repository for an entity.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>The DbContext must already be registered as a scoped service.</remarks>
    public static IServiceCollection AddMecesoftRepository<TContext, TEntity>(
        this IServiceCollection services)
        where TContext : DbContext
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IRepository<TEntity>, Repository<TEntity, TContext>>();
        return services;
    }

    /// <summary>
    /// Registers a scoped general-purpose unit of work for a DbContext.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>The DbContext must already be registered as a scoped service.</remarks>
    public static IServiceCollection AddMecesoftUnitOfWork<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IUnitOfWork, UnitOfWork<TContext>>();
        return services;
    }

    /// <summary>
    /// Registers a standard DbContext and a scoped general-purpose unit of work.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The provider configuration callback.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentException">
    /// The context derives from <c>MecesoftDbContext</c>; use the secure registration API instead.
    /// </exception>
    public static IServiceCollection AddMecesoftData<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddMecesoftData<TContext>((_, options) => configure(options));
    }

    /// <summary>
    /// Registers a standard DbContext and a scoped general-purpose unit of work.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The provider configuration callback with service-provider access.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentException">
    /// The context derives from <c>MecesoftDbContext</c>; use the secure registration API instead.
    /// </exception>
    public static IServiceCollection AddMecesoftData<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (typeof(Mecesoft.Data.EntityFrameworkCore.MecesoftDbContext).IsAssignableFrom(typeof(TContext)))
        {
            throw new ArgumentException(
                "MecesoftDbContext requires tenant scope services. Register it with "
                + "AddMecesoftDbContext and use the secure repositories.",
                nameof(TContext));
        }

        services.AddDbContext<TContext>(
            (serviceProvider, options) => configure(serviceProvider, options),
            ServiceLifetime.Scoped,
            ServiceLifetime.Scoped);
        services.AddMecesoftUnitOfWork<TContext>();
        return services;
    }

    /// <summary>
    /// Registers a database-per-tenant standard DbContext and a scoped unit of work.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <typeparam name="TResolver">The scoped tenant connection-string resolver type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures the provider with the connection string resolved for the current tenant scope.
    /// </param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// <para>
    /// This is the supported database-per-tenant setup for applications that do not need row-level
    /// tenant filters. The resolver is scoped, so the resolved database cannot leak between scopes.
    /// </para>
    /// <para>
    /// The resolver must fail closed when no tenant is resolved. Do not return a shared fallback
    /// database for an unresolved tenant.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMecesoftTenantData<TContext, TResolver>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder, string> configure)
        where TContext : DbContext
        where TResolver : class, ITenantConnectionStringResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (typeof(Mecesoft.Data.EntityFrameworkCore.MecesoftDbContext).IsAssignableFrom(typeof(TContext)))
        {
            throw new ArgumentException(
                "MecesoftDbContext uses row-level tenant filters. Database-per-tenant contexts "
                + "must be standard DbContext types.",
                nameof(TContext));
        }

        services.TryAddScoped<ITenantConnectionStringResolver, TResolver>();
        services.AddDbContext<TContext>(
            (serviceProvider, options) =>
            {
                string connectionString = serviceProvider
                    .GetRequiredService<ITenantConnectionStringResolver>()
                    .ResolveConnectionString();
                configure(options, connectionString);
            },
            ServiceLifetime.Scoped,
            ServiceLifetime.Scoped);
        services.AddMecesoftUnitOfWork<TContext>();
        return services;
    }
}
