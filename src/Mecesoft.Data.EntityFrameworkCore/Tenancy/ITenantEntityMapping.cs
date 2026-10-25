using System.Linq.Expressions;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Describes how one entity type participates in the mandatory tenant query filter.
/// </summary>
public interface ITenantEntityMapping
{
    /// <summary>
    /// Gets the entity CLR type.
    /// </summary>
    Type EntityType { get; }

    /// <summary>
    /// Gets the tenant identifier CLR type.
    /// </summary>
    /// <remarks>
    /// The default value is <see cref="object"/>, which is treated as string-like and requires a
    /// runtime collation verifier. Custom mappings should override this member with the real tenant
    /// identifier type. The adapter validates the declared type against the actual mapped EF property
    /// and rejects mismatches, shadow properties, value converters, and computed columns.
    /// </remarks>
    Type TenantIdType => typeof(object);

    /// <summary>
    /// Gets the direct root-entity tenant property name.
    /// </summary>
    /// <remarks>
    /// The property must be the entity's <c>ITenantEntity&lt;T&gt;.TenantId</c> contract property.
    /// The adapter builds the actual tenant equality filter from this validated metadata rather than
    /// trusting <see cref="BuildFilter"/>.
    /// </remarks>
    string TenantPropertyName { get; }

    /// <summary>
    /// Gets the tenant identifier from an entity instance for SaveChanges validation.
    /// </summary>
    /// <param name="entity">The entity instance.</param>
    /// <returns>The tenant identifier.</returns>
    object? GetTenantId(object entity);

    /// <summary>
    /// Builds the named tenant query filter for the supplied context.
    /// </summary>
    /// <param name="context">The DbContext whose current tenant value is used at query time.</param>
    /// <returns>A predicate expression for the entity.</returns>
    /// <remarks>
    /// The adapter validates that this expression contains the direct
    /// <see cref="TenantPropertyName"/> equality predicate, but it builds and installs its own
    /// canonical equality filter and never trusts this output as the tenant isolation boundary.
    /// </remarks>
    LambdaExpression BuildFilter(MecesoftDbContext context);
}
