using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Specifications;

/// <summary>
/// Describes a strongly typed projection from an entity to a result.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TResult">The projection result type.</typeparam>
/// <remarks>
/// An adapter must reject untranslatable projections with <see cref="UnsupportedExpressionFailure"/>
/// and must not silently evaluate them client-side.
/// </remarks>
public interface IProjection<TEntity, TResult>
{
    /// <summary>
    /// Gets the projection expression.
    /// </summary>
    Expression<Func<TEntity, TResult>> Selector { get; }
}

/// <summary>
/// Combines filtering, ordering, and a strongly typed projection.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TResult">The projection result type.</typeparam>
public interface IProjectionSpecification<TEntity, TResult>
    : ISpecification<TEntity>, IProjection<TEntity, TResult>;
