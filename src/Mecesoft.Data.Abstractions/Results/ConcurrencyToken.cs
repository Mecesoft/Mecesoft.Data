namespace Mecesoft.Data.Abstractions.Results;

/// <summary>
/// Represents an opaque, provider-neutral optimistic concurrency token.
/// </summary>
/// <remarks>
/// Adapters own the stable conversion between their native token and this value.
/// The token is not an authorization credential.
/// </remarks>
public sealed record ConcurrencyToken
{
    /// <summary>
    /// Initializes a new concurrency token.
    /// </summary>
    /// <param name="value">A non-empty opaque token value.</param>
    public ConcurrencyToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>
    /// Gets the opaque token value.
    /// </summary>
    public string Value { get; }
}
