namespace Mecesoft.Data.Abstractions.Auditing;

/// <summary>
/// Exposes optional creation audit metadata.
/// </summary>
/// <remarks>
/// Implementing this interface does not configure auditing. Register the EF Core audit interceptor
/// or set the values in application code. Mecesoft.Data never requires audit fields on an entity.
/// </remarks>
public interface ICreationAuditable
{
    /// <summary>
    /// Gets or sets the creation timestamp in UTC.
    /// </summary>
    DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the user that created the entity.
    /// </summary>
    string? CreatedBy { get; set; }
}

/// <summary>
/// Exposes optional modification audit metadata.
/// </summary>
/// <remarks>
/// Implementing this interface does not configure auditing. Register the EF Core audit interceptor
/// or set the values in application code. Mecesoft.Data never requires audit fields on an entity.
/// </remarks>
public interface IModificationAuditable
{
    /// <summary>
    /// Gets or sets the last modification timestamp in UTC.
    /// </summary>
    DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the user that last modified the entity.
    /// </summary>
    string? UpdatedBy { get; set; }
}

/// <summary>
/// Combines optional creation and modification audit metadata.
/// </summary>
public interface IAuditable : ICreationAuditable, IModificationAuditable
{
}

/// <summary>
/// Supplies the current audit user to the optional EF Core audit interceptor.
/// </summary>
/// <remarks>
/// Implementations must return a non-sensitive identifier. Returning <see langword="null"/> is
/// valid for anonymous or system operations.
/// </remarks>
public interface IAuditUserProvider
{
    /// <summary>
    /// Gets the current user identifier, or <see langword="null"/> when no user is available.
    /// </summary>
    string? UserId { get; }
}
