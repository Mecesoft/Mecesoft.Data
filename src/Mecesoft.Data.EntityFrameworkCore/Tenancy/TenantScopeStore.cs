using System.Runtime.CompilerServices;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Stores the tenant scope resolved for the current dependency-injection scope.
/// </summary>
/// <remarks>
/// The store is intended to be registered as scoped and shared by the tenant resolver coordinator,
/// the DbContext, and future repositories. It is mutable request-local state, not an authorization
/// credential.
/// </remarks>
public interface ITenantScopeStore
{
    /// <summary>
    /// Gets the resolved tenant identifier, or <see langword="null"/> when no scope exists.
    /// </summary>
    object? TenantId { get; }

    /// <summary>
    /// Gets the operation for which the scope was resolved, or <see langword="null"/> when no scope exists.
    /// </summary>
    TenantOperation? Operation { get; }

    /// <summary>
    /// Gets a value indicating whether a tenant identifier has been resolved.
    /// </summary>
    bool HasTenant { get; }

    /// <summary>
    /// Attempts to set the current tenant scope.
    /// </summary>
    /// <param name="tenantId">The resolved tenant identifier.</param>
    /// <param name="operation">The operation for which the identifier was resolved.</param>
    /// <returns>
    /// <see langword="true"/> when the scope was set or the same tenant was set again; otherwise,
    /// <see langword="false"/> when a different tenant is already active in this scope or a string
    /// tenant identifier is not canonical.
    /// </returns>
    /// <remarks>
    /// Setting a tenant scope does not authorize a write or a read. Only a scope resolved through
    /// <see cref="TenantScopeCoordinator{TEntity,TTenantId}"/> carries the resolver-issued
    /// authorization. Reads additionally require a context-bound read grant issued by the coordinator;
    /// a direct <c>TrySet</c> cannot forge one.
    /// </remarks>
    bool TrySet(object tenantId, TenantOperation operation);

    /// <summary>
    /// Clears the current tenant scope.
    /// </summary>
    void Clear();
}

/// <summary>
/// Default scoped tenant-scope store.
/// </summary>
public sealed class TenantScopeStore : ITenantScopeStore
{
    private object? _tenantId;
    private TenantOperation? _operation;

    /// <inheritdoc />
    public object? TenantId => _tenantId;

    /// <inheritdoc />
    public TenantOperation? Operation => _operation;

    /// <inheritdoc />
    public bool HasTenant => _tenantId is not null;

    /// <inheritdoc />
    public bool TrySet(object tenantId, TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "The tenant operation is not defined.");
        }

        if (tenantId is string text && !TenantIdValidation.IsCanonical(text))
        {
            return false;
        }

        if (tenantId is Guid guid && guid == Guid.Empty)
        {
            return false;
        }

        if (_tenantId is not null && !Equals(_tenantId, tenantId))
        {
            return false;
        }

        _tenantId = tenantId;
        _operation = operation;
        TenantWriteAuthorizationRegistry.Clear(this);
        return true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _tenantId = null;
        _operation = null;
        TenantWriteAuthorizationRegistry.Clear(this);
    }

    internal void AuthorizeWriteForTesting(
        object tenantId,
        TenantOperation operation)
    {
        if (!TrySet(tenantId, operation))
        {
            throw new InvalidOperationException(
                "The test write authorization could not be granted.");
        }

        TenantWriteAuthorizationRegistry.Set(
            this,
            new TenantWriteAuthorization(tenantId, operation));
    }
}

internal sealed record TenantWriteAuthorization(
    object TenantId,
    TenantOperation Operation,
    MecesoftDbContext? Context = null);

internal static class TenantWriteAuthorizationRegistry
{
    private static readonly ConditionalWeakTable<ITenantScopeStore, TenantWriteAuthorization>
        Authorizations = new();

    private static readonly object Sync = new();

    public static TenantWriteAuthorization? Get(ITenantScopeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Authorizations.TryGetValue(store, out TenantWriteAuthorization? authorization)
            ? authorization
            : null;
    }

    public static void Set(
        ITenantScopeStore store,
        TenantWriteAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorization);

        lock (Sync)
        {
            // Do not replace an already context-bound grant with an unbound one.
            if (authorization.Context is null
                && Authorizations.TryGetValue(
                    store,
                    out TenantWriteAuthorization? existing)
                && existing.Context is not null
                && existing.Operation == authorization.Operation
                && Equals(existing.TenantId, authorization.TenantId))
            {
                return;
            }

            Authorizations.Remove(store);
            Authorizations.Add(store, authorization);
        }
    }

    public static bool TryAuthorize(
        ITenantScopeStore store,
        MecesoftDbContext context,
        object tenantId,
        TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantId);

        lock (Sync)
        {
            if (!Authorizations.TryGetValue(
                store,
                out TenantWriteAuthorization? authorization)
                || authorization.Operation != operation
                || !Equals(authorization.TenantId, tenantId))
            {
                return false;
            }

            if (authorization.Context is not null)
            {
                return ReferenceEquals(authorization.Context, context);
            }

            Authorizations.Remove(store);
            Authorizations.Add(
                store,
                authorization with { Context = context });
            return true;
        }
    }

    public static void Clear(ITenantScopeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (Sync)
        {
            Authorizations.Remove(store);
        }
    }

    public static void ClearForContext(
        ITenantScopeStore store,
        MecesoftDbContext context)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);

        lock (Sync)
        {
            if (Authorizations.TryGetValue(
                store,
                out TenantWriteAuthorization? authorization)
                && (authorization.Context is null
                    || ReferenceEquals(authorization.Context, context)))
            {
                Authorizations.Remove(store);
            }
        }
    }
}
