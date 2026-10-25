using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Authorization;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mecesoft.Data.EntityFrameworkCore.DependencyInjection;

/// <summary>
/// Dependency-injection registration helpers for the EF Core foundation.
/// </summary>
/// <remarks>
/// Contexts, tenant scope stores, tenant coordinators, and units of work are scoped. DbContext
/// pooling is intentionally not used.
/// </remarks>
public static class MecesoftServiceCollectionExtensions
{
    /// <summary>
    /// Registers a scoped Mecesoft DbContext and the scoped foundation services.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The provider configuration callback.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMecesoftDbContext<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure)
        where TContext : MecesoftDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddMecesoftDbContext<TContext>((_, options) => configure(options));
    }

    /// <summary>
    /// Registers a scoped Mecesoft DbContext and the scoped foundation services.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The provider configuration callback with service-provider access.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMecesoftDbContext<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TContext : MecesoftDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDbContext<TContext>(
            (serviceProvider, options) => configure(serviceProvider, options),
            ServiceLifetime.Scoped,
            ServiceLifetime.Scoped);

        MecesoftContextRegistry registry = services
            .Where(descriptor => descriptor.ServiceType == typeof(MecesoftContextRegistry))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<MecesoftContextRegistry>()
            .FirstOrDefault() ?? new MecesoftContextRegistry();

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(MecesoftContextRegistry)))
        {
            services.AddSingleton(registry);
        }

        registry.Register(typeof(TContext));

        services.TryAddScoped<ITenantScopeStore, TenantScopeStore>();
        services.TryAddKeyedScoped<IUnitOfWork, EfUnitOfWork<TContext>>(typeof(TContext));
        services.TryAddScoped<IUnitOfWork>(ResolveUnkeyedUnitOfWork);
        services.TryAddScoped(typeof(TenantScopeCoordinator<,>));
        return services;
    }

    private static IUnitOfWork ResolveUnkeyedUnitOfWork(IServiceProvider serviceProvider)
    {
        MecesoftContextRegistry registry =
            serviceProvider.GetRequiredService<MecesoftContextRegistry>();
        Type[] contextTypes = registry.GetContextTypes();

        if (contextTypes.Length != 1)
        {
            throw new InvalidOperationException(
                "IUnitOfWork is ambiguous when multiple DbContext types are registered. "
                + "Resolve the context-specific keyed IUnitOfWork instead.");
        }

        return serviceProvider.GetRequiredKeyedService<IUnitOfWork>(contextTypes[0]);
    }

    /// <summary>
    /// Registers the mandatory tenant mapping for an entity.
    /// </summary>
    /// <typeparam name="TEntity">The tenant entity type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="tenantSelector">The tenant selector.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddTenantEntity<TEntity, TTenantId>(
        this IServiceCollection services,
        Expression<Func<TEntity, TTenantId>> tenantSelector)
        where TEntity : class, ITenantEntity<TTenantId>
        where TTenantId : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ITenantEntityMapping>(
            new TenantEntityMapping<TEntity, TTenantId>(tenantSelector));
        return services;
    }

    /// <summary>
    /// Registers an optional explicit soft-delete filter for an entity.
    /// </summary>
    /// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="visibilityPredicate">
    /// The visibility predicate. When omitted, <c>entity =&gt; !entity.IsDeleted</c> is used.
    /// </param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddSoftDeleteEntity<TEntity>(
        this IServiceCollection services,
        Expression<Func<TEntity, bool>>? visibilityPredicate = null)
        where TEntity : class, ISoftDeletable
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISoftDeleteEntityMapping>(
            new SoftDeleteEntityMapping<TEntity>(visibilityPredicate));
        return services;
    }

    /// <summary>
    /// Registers a scoped read repository for a tenant-owned entity.
    /// </summary>
    /// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
    /// <typeparam name="TEntity">The tenant entity type.</typeparam>
    /// <typeparam name="TKey">The entity key type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMecesoftReadRepository<TContext, TEntity, TKey, TTenantId>(
        this IServiceCollection services,
        Expression<Func<TEntity, TKey>> keySelector)
        where TContext : MecesoftDbContext
        where TEntity : class, ITenantEntity<TTenantId>
        where TKey : notnull
        where TTenantId : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.TryAddScoped<IReadRepository<TEntity, TKey>>(
            serviceProvider => new EfReadRepository<TContext, TEntity, TKey, TTenantId>(
                serviceProvider.GetRequiredService<TContext>(),
                serviceProvider.GetRequiredService<TenantScopeCoordinator<TEntity, TTenantId>>(),
                keySelector));

        return services;
    }

    /// <summary>
    /// Registers a scoped write repository for a tenant-owned entity.
    /// </summary>
    /// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
    /// <typeparam name="TEntity">The tenant entity type.</typeparam>
    /// <typeparam name="TKey">The entity key type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddMecesoftWriteRepository<TContext, TEntity, TKey, TTenantId>(
        this IServiceCollection services,
        Expression<Func<TEntity, TKey>> keySelector)
        where TContext : MecesoftDbContext
        where TEntity : class, ITenantEntity<TTenantId>
        where TKey : notnull
        where TTenantId : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.TryAddScoped<IRepository<TEntity, TKey>>(
            serviceProvider => new EfWriteRepository<TContext, TEntity, TKey, TTenantId>(
                serviceProvider.GetRequiredService<TContext>(),
                serviceProvider.GetRequiredService<TenantScopeCoordinator<TEntity, TTenantId>>(),
                serviceProvider.GetRequiredKeyedService<IUnitOfWork>(typeof(TContext)),
                keySelector));

        return services;
    }

    /// <summary>
    /// Registers a scoped soft-delete repository for a tenant-owned, soft-deletable entity.
    /// </summary>
    /// <typeparam name="TContext">The Mecesoft DbContext type.</typeparam>
    /// <typeparam name="TEntity">The soft-deletable tenant entity type.</typeparam>
    /// <typeparam name="TKey">The entity key type.</typeparam>
    /// <typeparam name="TTenantId">The tenant identifier type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="keySelector">The unique primary-key selector.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// The caller must also register an <see cref="ISoftDeleteAuthorizer{TEntity,TKey,TTenantId}"/>.
    /// Repository resolution fails closed when no authorizer is registered.
    /// </remarks>
    public static IServiceCollection AddMecesoftSoftDeleteRepository<TContext, TEntity, TKey, TTenantId>(
        this IServiceCollection services,
        Expression<Func<TEntity, TKey>> keySelector)
        where TContext : MecesoftDbContext
        where TEntity : class, ISoftDeletable, ITenantEntity<TTenantId>
        where TKey : notnull
        where TTenantId : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.TryAddScoped<ISoftDeleteRepository<TEntity, TKey>>(
            serviceProvider => new EfSoftDeleteRepository<TContext, TEntity, TKey, TTenantId>(
                serviceProvider.GetRequiredService<TContext>(),
                serviceProvider.GetRequiredService<TenantScopeCoordinator<TEntity, TTenantId>>(),
                serviceProvider.GetRequiredKeyedService<IUnitOfWork>(typeof(TContext)),
                serviceProvider.GetRequiredService<ISoftDeleteAuthorizer<TEntity, TKey, TTenantId>>(),
                keySelector));

        return services;
    }

    /// <summary>
    /// Registers an explicit global write authorizer and the scoped global write coordinator.
    /// </summary>
    /// <typeparam name="TAuthorizer">The trusted global write authorizer.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// Global writes remain denied by default until this registration is present and the
    /// coordinator explicitly authorizes each global root entity write.
    /// </remarks>
    public static IServiceCollection AddMecesoftGlobalWriteAuthorization<TAuthorizer>(
        this IServiceCollection services)
        where TAuthorizer : class, IGlobalWriteAuthorizer
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IGlobalWriteAuthorizer, TAuthorizer>();
        services.TryAddScoped<GlobalWriteCoordinator>();
        return services;
    }
}
