namespace Mecesoft.Data.Abstractions.SoftDelete;

/// <summary>
/// Provides optional soft-delete and restore operations for a non-tenant-specific DbContext.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
/// <typeparam name="TKey">The entity key type.</typeparam>
/// <remarks>
/// <para>
/// This compatibility-oriented service is intended for contexts that do not use tenant isolation.
/// It removes only the named <c>Mecesoft.SoftDelete</c> query filter and preserves any other filter,
/// including a tenant filter. It fails closed when that named filter is not configured.
/// </para>
/// <para>
/// The service performs no authorization. Applications must authorize the operation before calling
/// it. When the context is a <c>MecesoftDbContext</c>, use the secure soft-delete repository instead;
/// this service refuses to run against a <c>MecesoftDbContext</c> so the tenant authorization and
/// audit pipeline cannot be bypassed.
/// </para>
/// </remarks>
public interface ISoftDeleteService<TEntity, in TKey>
    where TEntity : class, ISoftDeletable
    where TKey : notnull
{
    /// <summary>
    /// Marks an active entity as deleted and saves the change.
    /// </summary>
    /// <param name="key">The entity key.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the entity exists (including when it was already deleted);
    /// <see langword="false"/> when no matching row exists.
    /// </returns>
    ValueTask<bool> SoftDeleteAsync(
        TKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a deleted entity and saves the change.
    /// </summary>
    /// <param name="key">The entity key.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the entity exists (including when it was already active);
    /// <see langword="false"/> when no matching row exists.
    /// </returns>
    ValueTask<bool> RestoreAsync(
        TKey key,
        CancellationToken cancellationToken = default);
}
