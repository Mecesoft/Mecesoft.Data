using System.Linq.Expressions;

namespace Mecesoft.Data.Abstractions.Specifications;

/// <summary>
/// Specifies a sort direction.
/// </summary>
public enum SortDirection
{
    /// <summary>
    /// Sorts from the lowest value to the highest value.
    /// </summary>
    Ascending,

    /// <summary>
    /// Sorts from the highest value to the lowest value.
    /// </summary>
    Descending,
}

/// <summary>
/// Represents a type-preserving sort expression.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface ISortExpression<TEntity>
{
    /// <summary>
    /// Gets the key selector without erasing its native key type to <see cref="object"/>.
    /// </summary>
    LambdaExpression KeySelector { get; }

    /// <summary>
    /// Gets the sort direction.
    /// </summary>
    SortDirection Direction { get; }
}

/// <summary>
/// Represents a strongly typed sort expression.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The sort key type.</typeparam>
public sealed class SortExpression<TEntity, TKey> : ISortExpression<TEntity>
{
    /// <summary>
    /// Initializes a new sort expression.
    /// </summary>
    /// <param name="keySelector">The strongly typed sort key selector.</param>
    /// <param name="direction">The sort direction.</param>
    public SortExpression(Expression<Func<TEntity, TKey>> keySelector, SortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        KeySelector = keySelector;
        Direction = direction;
    }

    /// <summary>
    /// Gets the strongly typed key selector.
    /// </summary>
    public Expression<Func<TEntity, TKey>> KeySelector { get; }

    /// <inheritdoc />
    LambdaExpression ISortExpression<TEntity>.KeySelector => KeySelector;

    /// <inheritdoc />
    public SortDirection Direction { get; }
}
