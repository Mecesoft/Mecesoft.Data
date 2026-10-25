namespace Mecesoft.Data.Abstractions.Repositories;

/// <summary>
/// Identifies an extensible adapter capability without coupling the core package to an adapter.
/// </summary>
public sealed record DataAdapterCapability
{
    /// <summary>
    /// Initializes a new capability identifier.
    /// </summary>
    /// <param name="name">A stable, namespaced capability name.</param>
    public DataAdapterCapability(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>
    /// Gets the stable capability name.
    /// </summary>
    public string Name { get; }
}

/// <summary>
/// Exposes capabilities implemented by a concrete data adapter.
/// </summary>
/// <remarks>
/// Future Dapper and bulk packages can publish their own capability identifiers and separate
/// operation interfaces without expanding <see cref="IRepository{TEntity,TKey}"/>.
/// </remarks>
public interface IDataAdapterCapabilities
{
    /// <summary>
    /// Gets the immutable snapshot of advertised capabilities.
    /// </summary>
    IReadOnlyCollection<DataAdapterCapability> Capabilities { get; }

    /// <summary>
    /// Determines whether the adapter supports a capability.
    /// </summary>
    /// <param name="capability">The capability to test.</param>
    /// <returns><see langword="true"/> when supported; otherwise, <see langword="false"/>.</returns>
    bool Supports(DataAdapterCapability capability);
}
