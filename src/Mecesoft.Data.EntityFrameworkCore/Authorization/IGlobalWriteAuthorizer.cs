using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Authorization;

/// <summary>
/// Authorizes explicit writes to global (shared) entities.
/// </summary>
/// <remarks>
/// <para>
/// Global entities are not tenant-filtered and therefore cannot use a tenant write grant. Global
/// writes are denied by default and require an implementation of this interface that makes an
/// explicit, resource-level decision.
/// </para>
/// <para>
/// This interface is a trusted security component. Returning success for an unauthorized write
/// disables the global write boundary. Implementations must fail closed on missing configuration,
/// unavailable authorization state, or incomplete resource data.
/// </para>
/// </remarks>
public interface IGlobalWriteAuthorizer
{
    /// <summary>
    /// Authorizes one global write operation.
    /// </summary>
    /// <param name="context">The global entity and requested write operation.</param>
    /// <param name="cancellationToken">The token used to cancel authorization.</param>
    /// <returns>
    /// Success with <see langword="true"/> when the write is allowed; success with
    /// <see langword="false"/> or a typed failure denies the write.
    /// </returns>
    ValueTask<DataResult<bool>> AuthorizeAsync(
        GlobalWriteAuthorizationContext context,
        CancellationToken cancellationToken = default);
}
