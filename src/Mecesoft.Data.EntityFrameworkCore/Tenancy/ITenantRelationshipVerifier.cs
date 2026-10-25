using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Verifies that database foreign-key delete actions match the EF model's relationship policy.
/// </summary>
/// <remarks>
/// <para>
/// The verifier must inspect the actual database catalog using schema-qualified table and column
/// identities. Missing, ambiguous, or mismatched metadata must fail closed. The verifier must not
/// modify the database or execute tenant-filtered Entity Framework queries.
/// </para>
/// <para>
/// This interface is a trusted security component. An implementation that accepts a database
/// cascade that is not represented by the EF model can bypass central <c>SaveChanges</c>
/// validation.
/// </para>
/// </remarks>
public interface ITenantRelationshipVerifier
{
    /// <summary>
    /// Verifies all model foreign-key delete actions against the database catalog.
    /// </summary>
    /// <param name="context">The context whose model and database connection are verified.</param>
    /// <returns>Success when every destructive database action is represented by the model; otherwise a typed failure.</returns>
    DataResult<bool> VerifyRelationships(MecesoftDbContext context);
}
