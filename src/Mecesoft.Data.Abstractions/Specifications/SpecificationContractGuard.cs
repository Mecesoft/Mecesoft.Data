using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Specifications;

/// <summary>
/// Provides fail-closed validation helpers for query specifications.
/// </summary>
/// <remarks>
/// These helpers validate structure only. They do not prove that an expression is translatable, that
/// ordering is unique, or that an adapter applies the required tie-breaker.
/// </remarks>
public static class SpecificationContractGuard
{
    /// <summary>
    /// Validates that a specification has at least one usable sort expression for pagination.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="specification">The specification to validate.</param>
    /// <returns>Success when a sort exists; otherwise, an <see cref="UnstableOrderingFailure"/>.</returns>
    /// <remarks>
    /// A non-empty sort list is necessary but not sufficient for stable pagination. The adapter must
    /// also append a unique tie-breaker, normally the entity primary key, before applying offset or
    /// keyset pagination. This helper does not prove uniqueness.
    /// </remarks>
    public static DataResult<bool> ValidateForPagination<TEntity>(ISpecification<TEntity> specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        if (specification.Sorts is null || specification.Sorts.Count == 0)
        {
            return DataResult.Failed<bool>(
                new UnstableOrderingFailure(
                    "Pagination requires at least one sort expression."));
        }

        foreach (ISortExpression<TEntity> sort in specification.Sorts)
        {
            if (sort is null || sort.KeySelector is null)
            {
                return DataResult.Failed<bool>(
                    new UnsupportedExpressionFailure(
                        "sort",
                        "A sort expression is missing or invalid."));
            }
        }

        return DataResult.Succeeded(true);
    }
}
