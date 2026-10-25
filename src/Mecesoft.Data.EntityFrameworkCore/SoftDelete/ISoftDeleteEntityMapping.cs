using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;

namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Describes an explicit soft-delete filter for one entity type.
/// </summary>
public interface ISoftDeleteEntityMapping
{
    /// <summary>
    /// Gets the entity CLR type.
    /// </summary>
    Type EntityType { get; }

    /// <summary>
    /// Builds the named soft-delete query filter.
    /// </summary>
    /// <returns>A predicate expression that returns <see langword="true"/> for visible rows.</returns>
    LambdaExpression BuildFilter();
}

/// <summary>
/// Strongly typed soft-delete mapping.
/// </summary>
/// <typeparam name="TEntity">The soft-deletable entity type.</typeparam>
public sealed class SoftDeleteEntityMapping<TEntity> : ISoftDeleteEntityMapping
    where TEntity : class, ISoftDeletable
{
    /// <summary>
    /// Initializes a new soft-delete mapping.
    /// </summary>
    /// <param name="visibilityPredicate">
    /// The predicate that returns <see langword="true"/> for visible rows. When omitted,
    /// <c>entity =&gt; !entity.IsDeleted</c> is used.
    /// </param>
    public SoftDeleteEntityMapping(Expression<Func<TEntity, bool>>? visibilityPredicate = null)
    {
        VisibilityPredicate = visibilityPredicate ?? (entity => !entity.IsDeleted);
    }

    /// <summary>
    /// Gets the visibility predicate.
    /// </summary>
    public Expression<Func<TEntity, bool>> VisibilityPredicate { get; }

    /// <inheritdoc />
    public Type EntityType => typeof(TEntity);

    /// <inheritdoc />
    public LambdaExpression BuildFilter() => VisibilityPredicate;
}
