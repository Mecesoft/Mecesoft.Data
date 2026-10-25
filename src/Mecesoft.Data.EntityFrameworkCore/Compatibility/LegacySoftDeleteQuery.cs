using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Repository;

/// <summary>
/// Applies the compatibility layer's deliberately narrow deleted-row visibility rule.
/// </summary>
internal static class LegacySoftDeleteQuery
{
    private const string IncludeDeletedGuidance =
        "includeDeleted is supported only for ISoftDeletable entities whose model has the named "
        + "'Mecesoft.SoftDelete' query filter. Call ModelBuilder.ApplyMecesoftSoftDeleteFilters() "
        + "in a standard DbContext. MecesoftDbContext deleted rows must be read through the "
        + "authorized soft-delete repository.";

    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> query,
        DbContext context,
        bool includeDeleted)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);

        if (!includeDeleted)
        {
            return query;
        }

        if (!typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity)))
        {
            throw new InvalidOperationException(
                "includeDeleted is supported only for entities that implement ISoftDeletable. "
                + "The legacy behavior of removing every query filter is not reproduced because it "
                + "could silently disable a tenant filter.");
        }

        if (context is MecesoftDbContext)
        {
            throw new InvalidOperationException(
                "includeDeleted is not available on MecesoftDbContext. Use the authorized "
                + "soft-delete/restore repository so tenant, resource authorization, and "
                + "concurrency checks run.");
        }

        IEntityType? entityType = context.Model.FindEntityType(typeof(TEntity));
        if (entityType is null)
        {
            throw new InvalidOperationException(
                $"The entity type '{typeof(TEntity).Name}' is not part of the DbContext model.");
        }

        if (!HasNamedSoftDeleteFilter(entityType))
        {
            throw new InvalidOperationException(IncludeDeletedGuidance);
        }

        return query.IgnoreQueryFilters([MecesoftQueryFilterKeys.SoftDelete]);
    }

    private static bool HasNamedSoftDeleteFilter(IEntityType entityType)
    {
        IReadOnlyEntityType? current = entityType;
        while (current is not null)
        {
            if (current.FindDeclaredQueryFilter(MecesoftQueryFilterKeys.SoftDelete) is not null)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}
