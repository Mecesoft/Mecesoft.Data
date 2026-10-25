using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mecesoft.Data.EntityFrameworkCore.Modeling;

/// <summary>
/// Configures explicitly shared, non-tenant-owned lookup entities.
/// </summary>
/// <remarks>
/// A global entity is shared by every tenant and is never tenant-filtered. Declaring an entity as
/// global is an explicit security decision: the entity must not implement a tenant contract, must
/// not have a tenant mapping, and must not reference a tenant-owned entity through a foreign key.
/// A tenant-owned entity may reference a global entity.
/// </remarks>
public static class GlobalEntityModelBuilderExtensions
{
    internal const string AnnotationName = "Mecesoft:GlobalEntity";

    /// <summary>
    /// Marks an entity as an explicitly shared global entity.
    /// </summary>
    /// <typeparam name="TEntity">The global entity type.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <returns>The same builder.</returns>
    public static EntityTypeBuilder<TEntity> IsGlobal<TEntity>(
        this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(AnnotationName, true);
        return builder;
    }
}
