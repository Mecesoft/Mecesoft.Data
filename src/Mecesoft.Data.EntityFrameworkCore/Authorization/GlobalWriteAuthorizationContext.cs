using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Authorization;

/// <summary>
/// Describes one requested global entity write for a trusted authorizer.
/// </summary>
/// <remarks>
/// This object contains data only. Possessing or constructing a context does not grant authority;
/// only <see cref="GlobalWriteCoordinator"/> can issue a context-bound global write grant after the
/// authorizer returns success.
/// </remarks>
public sealed record GlobalWriteAuthorizationContext
{
    /// <summary>
    /// Initializes a global write authorization context.
    /// </summary>
    /// <param name="entity">The global root entity that will be written.</param>
    /// <param name="operation">The requested write operation.</param>
    public GlobalWriteAuthorizationContext(
        object entity,
        TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (operation is not (
            TenantOperation.Create
            or TenantOperation.Update
            or TenantOperation.HardDelete))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Global writes support Create, Update, and HardDelete only.");
        }

        Entity = entity;
        EntityType = entity.GetType();
        Operation = operation;
    }

    /// <summary>
    /// Gets the global root entity that will be written.
    /// </summary>
    public object Entity { get; }

    /// <summary>
    /// Gets the runtime CLR type of the global root entity.
    /// </summary>
    public Type EntityType { get; }

    /// <summary>
    /// Gets the requested write operation.
    /// </summary>
    public TenantOperation Operation { get; }
}
