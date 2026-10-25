namespace Mecesoft.Data.Abstractions.Results;

/// <summary>
/// Represents either a successful value or a typed data failure.
/// </summary>
/// <typeparam name="T">The successful value type.</typeparam>
public sealed class DataResult<T>
{
    private readonly T? _value;

    internal DataResult(T value)
    {
        IsSuccess = true;
        _value = value;
    }

    internal DataResult(DataFailure failure)
    {
        Failure = failure;
    }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Gets the successful value.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result represents a failure.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed data result does not contain a value.");

    /// <summary>
    /// Gets the failure, or <see langword="null"/> when the operation succeeded.
    /// </summary>
    public DataFailure? Failure { get; }

}

/// <summary>
/// Creates typed data results without static members on generic result types.
/// </summary>
public static class DataResult
{
    /// <summary>
    /// Creates a successful typed result.
    /// </summary>
    /// <typeparam name="T">The successful value type.</typeparam>
    /// <param name="value">The successful value.</param>
    /// <returns>A successful result.</returns>
    public static DataResult<T> Succeeded<T>(T value) => new(value);

    /// <summary>
    /// Creates a failed typed result.
    /// </summary>
    /// <typeparam name="T">The expected successful value type.</typeparam>
    /// <param name="failure">The typed failure.</param>
    /// <returns>A failed result.</returns>
    public static DataResult<T> Failed<T>(DataFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new DataResult<T>(failure);
    }
}
