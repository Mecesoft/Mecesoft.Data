using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Represents a SaveChanges attempt rejected by the central tenant or protected-field validation.
/// </summary>
public sealed class SaveChangesSecurityException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new SaveChanges security exception.
    /// </summary>
    /// <param name="failure">The typed security failure.</param>
    public SaveChangesSecurityException(DataFailure failure)
        : base(failure.Message)
    {
        Failure = failure;
    }

    /// <summary>
    /// Gets the typed security failure.
    /// </summary>
    public DataFailure Failure { get; }
}
