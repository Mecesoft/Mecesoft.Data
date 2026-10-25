using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore.Repositories;

/// <summary>
/// Represents a specification read that returned no entity.
/// </summary>
/// <param name="EntityName">The logical entity name.</param>
public sealed record ReadNotFoundFailure(string EntityName)
    : DataFailure("data.not_found", $"A {EntityName} entity matching the read query was not found.");
