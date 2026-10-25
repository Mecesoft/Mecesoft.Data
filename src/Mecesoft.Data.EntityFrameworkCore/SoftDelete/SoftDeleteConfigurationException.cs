namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Represents an invalid optional soft-delete configuration.
/// </summary>
public sealed class SoftDeleteConfigurationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new soft-delete configuration exception.
    /// </summary>
    /// <param name="message">A non-sensitive configuration description.</param>
    public SoftDeleteConfigurationException(string message)
        : base(message)
    {
    }
}
