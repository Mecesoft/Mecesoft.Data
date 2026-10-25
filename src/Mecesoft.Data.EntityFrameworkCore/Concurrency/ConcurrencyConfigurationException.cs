using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore.Concurrency;

/// <summary>
/// Represents an unsupported or missing EF Core concurrency-token configuration.
/// </summary>
public sealed class ConcurrencyConfigurationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new concurrency configuration exception.
    /// </summary>
    /// <param name="message">A non-sensitive configuration description.</param>
    public ConcurrencyConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents a fail-closed concurrency-token configuration failure.
/// </summary>
/// <param name="Reason">A non-sensitive configuration description.</param>
public sealed record ConcurrencyConfigurationFailure(string Reason)
    : DataFailure("data.concurrency_configuration_invalid", Reason);
