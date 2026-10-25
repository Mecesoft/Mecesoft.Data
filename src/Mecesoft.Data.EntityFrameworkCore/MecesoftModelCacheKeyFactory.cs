using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Produces a model-cache key that includes the tenant and soft-delete mapping signature.
/// </summary>
/// <remarks>
/// EF Core's default key is context-type based. Without the mapping signature, a context created
/// with a different mapping set could reuse a model whose filters and validation were built for the
/// first set. The signature is structural and immutable; it never uses mapping object references or
/// mutable delegates.
/// </remarks>
internal sealed class MecesoftModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        string mappingSignature = context is MecesoftDbContext mecesoft
            ? mecesoft.GetModelCacheKeySignature()
            : string.Empty;

        return new CacheKey(context.GetType(), designTime, mappingSignature);
    }

    private readonly record struct CacheKey(
        Type ContextType,
        bool DesignTime,
        string MappingSignature);
}
