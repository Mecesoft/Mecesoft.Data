using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Represents a persistence failure reported by the EF Core unit of work.
/// </summary>
/// <param name="Reason">A non-sensitive description suitable for diagnostics.</param>
public sealed record PersistenceFailure(string Reason)
    : DataFailure("data.persistence_failed", Reason);
