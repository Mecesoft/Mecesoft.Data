using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Queries;

/// <summary>
/// Carries the short-lived grant that allows the soft-delete repository to inspect deleted rows.
/// </summary>
internal static class SoftDeleteReadAuthorization
{
    private static readonly AsyncLocal<Grant?> Current = new();

    public static IDisposable Begin(
        object tenantId,
        SoftDeleteOperation operation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (operation is not (SoftDeleteOperation.SoftDelete
            or SoftDeleteOperation.Restore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Only controlled soft-delete transitions may inspect deleted rows.");
        }

        Grant? previous = Current.Value;
        Current.Value = new Grant(tenantId, operation);
        return new Scope(previous);
    }

    public static void Require()
    {
        if (Current.Value is null)
        {
            throw new TenantConfigurationException(
                "Deleted rows can only be read through an authorized soft-delete or restore scope.");
        }
    }

    private sealed record Grant(object TenantId, SoftDeleteOperation Operation);

    private sealed class Scope(Grant? previous) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
        }
    }
}

/// <summary>
/// Provides the intentionally narrow query-filter removal surface.
/// </summary>
/// <remarks>
/// There is deliberately no helper that removes the tenant filter. Only the soft-delete filter can
/// be removed through this API; the tenant filter remains active in the same query. The helper is
/// restricted to the controlled soft-delete repository scope; direct callers fail closed.
/// </remarks>
public static class TenantQueryableExtensions
{
    /// <summary>
    /// Removes only the named soft-delete filter from a query while preserving the tenant filter.
    /// </summary>
    /// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
    /// <param name="query">The source query.</param>
    /// <returns>The query without the soft-delete filter.</returns>
    public static IQueryable<TEntity> IgnoreSoftDelete<TEntity>(this IQueryable<TEntity> query)
        where TEntity : class, ISoftDeletable
    {
        ArgumentNullException.ThrowIfNull(query);
        SoftDeleteReadAuthorization.Require();
        return query.IgnoreQueryFilters([MecesoftQueryFilterKeys.SoftDelete]);
    }
}
