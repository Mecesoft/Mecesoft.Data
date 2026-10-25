using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Installs optional named soft-delete query filters on a standard DbContext.
/// </summary>
/// <remarks>
/// <para>
/// The filter key is <see cref="MecesoftQueryFilterKeys.SoftDelete"/>. Using a named filter lets the
/// compatibility repository and <see cref="SoftDeleteService{TContext,TEntity,TKey}"/> remove only
/// the deleted-row filter while keeping any tenant or application filter active.
/// </para>
/// <para>
/// This helper is not required for <c>MecesoftDbContext</c>; that context installs and validates its
/// own filters. Calling this helper does not add tenant isolation, authorization, or auditing.
/// </para>
/// </remarks>
public static class SoftDeleteModelBuilderExtensions
{
    /// <summary>
    /// Applies the default <c>!IsDeleted</c> named filter to every non-owned soft-deletable entity.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder.</returns>
    /// <exception cref="SoftDeleteConfigurationException">
    /// A soft-deletable entity does not map a writable boolean <c>IsDeleted</c> property.
    /// </exception>
    public static ModelBuilder ApplyMecesoftSoftDeleteFilters(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            Type? clrType = entityType.ClrType;
            if (clrType is null
                || entityType.IsOwned()
                || !typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                continue;
            }

            ApplyFilter(modelBuilder, clrType);
        }

        return modelBuilder;
    }

    /// <summary>
    /// Applies a named soft-delete filter to one entity type.
    /// </summary>
    /// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="visibilityPredicate">
    /// The visibility predicate. When omitted, <c>entity =&gt; !entity.IsDeleted</c> is used.
    /// </param>
    /// <returns>The same model builder.</returns>
    public static ModelBuilder ApplyMecesoftSoftDeleteFilter<TEntity>(
        this ModelBuilder modelBuilder,
        Expression<Func<TEntity, bool>>? visibilityPredicate = null)
        where TEntity : class, ISoftDeletable
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        EntityTypeBuilder<TEntity> builder = modelBuilder.Entity<TEntity>();
        builder.HasQueryFilter(
            MecesoftQueryFilterKeys.SoftDelete,
            visibilityPredicate ?? (entity => !entity.IsDeleted));
        return modelBuilder;
    }

    private static void ApplyFilter(ModelBuilder modelBuilder, Type clrType)
    {
        IMutableEntityType entityType = modelBuilder.Entity(clrType).Metadata;
        IMutableProperty? isDeleted = entityType.FindProperty(nameof(ISoftDeletable.IsDeleted));

        if (isDeleted is null
            || isDeleted.ClrType != typeof(bool)
            || isDeleted.PropertyInfo is null)
        {
            throw new SoftDeleteConfigurationException(
                $"The soft-deletable entity '{clrType.Name}' must map a boolean "
                + $"{nameof(ISoftDeletable.IsDeleted)} property.");
        }

        ParameterExpression parameter = Expression.Parameter(clrType, "entity");
        Expression body = Expression.Not(Expression.Property(parameter, isDeleted.PropertyInfo));
        modelBuilder.Entity(clrType).HasQueryFilter(
            MecesoftQueryFilterKeys.SoftDelete,
            Expression.Lambda(body, parameter));
    }
}
