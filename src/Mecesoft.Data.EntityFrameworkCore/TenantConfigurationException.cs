namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Represents a missing, duplicate, or invalid tenant/soft-delete mapping detected while building the model.
/// </summary>
public sealed class TenantConfigurationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new configuration exception.
    /// </summary>
    /// <param name="message">A non-sensitive configuration description.</param>
    public TenantConfigurationException(string message)
        : base(message)
    {
    }
}
