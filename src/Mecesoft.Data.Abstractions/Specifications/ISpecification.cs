using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Specifications;

/// <summary>
/// Describes provider-independent filtering and deterministic ordering for an entity query.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <remarks>
/// A data adapter must reject expression nodes that it cannot translate with
/// <see cref="UnsupportedExpressionFailure"/> and must never evaluate them client-side. Tenant and
/// soft-delete security predicates are adapter policies and must never be supplied solely by this
/// specification.
/// </remarks>
public interface ISpecification<TEntity>
{
    /// <summary>
    /// Gets the optional entity predicate. A <see langword="null"/> value selects all entities
    /// permitted by mandatory adapter policies.
    /// </summary>
    Expression<Func<TEntity, bool>>? Criteria { get; }

    /// <summary>
    /// Gets the ordered list of strongly typed sort expressions.
    /// </summary>
    /// <remarks>
    /// Pagination requires a stable order. Adapters must validate this list with
    /// <see cref="SpecificationContractGuard.ValidateForPagination{TEntity}"/> and append a unique
    /// tie-breaker, normally the primary key, before applying pagination.
    /// </remarks>
    IReadOnlyList<ISortExpression<TEntity>> Sorts { get; }
}
