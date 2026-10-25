using Mecesoft.Data.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mecesoft.Data.EntityFrameworkCore.Auditing;

/// <summary>
/// Dependency-injection helpers for optional audit fields.
/// </summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the optional audit user provider and save-changes interceptor.
    /// </summary>
    /// <typeparam name="TUserProvider">The application's audit user provider.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <remarks>
    /// The interceptor is scoped. Register it on the DbContext options with
    /// <see cref="AddMecesoftAuditing(DbContextOptionsBuilder, IServiceProvider)"/>.
    /// </remarks>
    public static IServiceCollection AddMecesoftAuditing<TUserProvider>(
        this IServiceCollection services)
        where TUserProvider : class, IAuditUserProvider
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAuditUserProvider, TUserProvider>();
        services.TryAddScoped<AuditSaveChangesInterceptor>();
        return services;
    }

    /// <summary>
    /// Adds the registered audit interceptor to the DbContext options.
    /// </summary>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder AddMecesoftAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return optionsBuilder.AddInterceptors(
            serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
    }

    /// <summary>
    /// Adds the registered audit interceptor to the DbContext options.
    /// </summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <param name="optionsBuilder">The options builder.</param>
    /// <param name="serviceProvider">The scoped service provider.</param>
    /// <returns>The same options builder.</returns>
    public static DbContextOptionsBuilder<TContext> AddMecesoftAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        optionsBuilder.AddInterceptors(
            serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
        return optionsBuilder;
    }
}
