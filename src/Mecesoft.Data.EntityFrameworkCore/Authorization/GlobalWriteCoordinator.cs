using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Authorization;

/// <summary>
/// Issues context-bound global write grants after explicit authorization.
/// </summary>
/// <remarks>
/// <para>
/// A global grant is valid only for the supplied context instance, the supplied root entity, and
/// the requested operation. It is consumed by the next <c>SaveChanges</c> call. The scoped tenant
/// store and <see cref="Tenancy.ITenantScopeStore.TrySet"/> cannot create a global write grant.
/// </para>
/// <para>
/// Authorize the root global entity. Owned entities are authorized through their global root
/// owner.
/// </para>
/// </remarks>
public sealed class GlobalWriteCoordinator
{
    private readonly IGlobalWriteAuthorizer _authorizer;

    /// <summary>
    /// Initializes a global write coordinator.
    /// </summary>
    /// <param name="authorizer">The application-provided global write authorizer.</param>
    public GlobalWriteCoordinator(IGlobalWriteAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        _authorizer = authorizer;
    }

    /// <summary>
    /// Authorizes one global entity write and binds the grant to the supplied context.
    /// </summary>
    /// <param name="context">The context that will execute <c>SaveChanges</c>.</param>
    /// <param name="entity">The global root entity that will be written.</param>
    /// <param name="operation">The requested write operation.</param>
    /// <param name="cancellationToken">The token used to cancel authorization.</param>
    /// <returns>Success when the context may write the entity; otherwise a typed denial.</returns>
    public async ValueTask<DataResult<bool>> AuthorizeAsync(
        MecesoftDbContext context,
        object entity,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);

        if (operation is not (
            TenantOperation.Create
            or TenantOperation.Update
            or TenantOperation.HardDelete))
        {
            return DataResult.Failed<bool>(
                new ValidationFailure(
                    "Global writes support Create, Update, and HardDelete only."));
        }

        DataResult<bool> authorization;
        try
        {
            authorization = await _authorizer
                .AuthorizeAsync(
                    new GlobalWriteAuthorizationContext(entity, operation),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DataResult.Failed<bool>(
                new AuthorizationFailure(
                    "global_write",
                    "The global write authorization failed."));
        }

        if (authorization.IsFailure)
        {
            return authorization;
        }

        if (!authorization.Value)
        {
            return DataResult.Failed<bool>(
                new AuthorizationFailure(
                    "global_write",
                    "The global write was denied."));
        }

        try
        {
            context.AuthorizeGlobalWrite(entity, operation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DataResult.Failed<bool>(
                new AuthorizationFailure(
                    "global_write",
                    exception.Message));
        }

        return DataResult.Succeeded(true);
    }
}
