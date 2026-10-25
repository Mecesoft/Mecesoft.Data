using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Verifies at runtime that a string tenant identifier is compared with a case-sensitive or
/// binary database collation.
/// </summary>
/// <remarks>
/// <para>
/// Model metadata and an entity mapping alone are not a security guarantee. A verifier must inspect
/// the actual database or column collation, or perform an equivalent runtime probe. Returning
/// success means the tenant predicate for the supplied mapping is safe for string identifiers.
/// </para>
/// <para>
/// The verifier is a trusted security component. Implementations must use the context connection
/// directly and must not execute tenant-filtered Entity Framework queries, which could recurse into
/// collation verification. A verifier failure must be treated as fail-closed.
/// </para>
/// </remarks>
public interface ITenantCollationVerifier
{
    /// <summary>
    /// Verifies the effective collation for one tenant mapping.
    /// </summary>
    /// <param name="context">The context whose database is being verified.</param>
    /// <param name="mapping">The tenant mapping whose tenant column must be verified.</param>
    /// <returns>Success when the tenant comparison is case-sensitive; otherwise a typed failure.</returns>
    DataResult<bool> Verify(
        MecesoftDbContext context,
        ITenantEntityMapping mapping);
}
