namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Marks a tenant collation verifier as a trusted production implementation.
/// </summary>
/// <remarks>
/// Provider mandatory-security configurations can require this marker so a test, reference, or
/// custom verifier cannot silently replace the reviewed production verifier. The marker is not a
/// cryptographic proof; the implementation must still be reviewed and tested.
/// </remarks>
public interface ITrustedTenantCollationVerifier
{
}
