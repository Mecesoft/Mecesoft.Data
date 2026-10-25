using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PgOrder : ITenantEntity<Guid>, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public Guid Version { get; set; }

    public List<PgOrderLine> Lines { get; } = [];
}

public sealed class PgOrderLine : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid TenantId { get; set; }

    public PgOrder? Order { get; set; }
}

public sealed class PgStringTenantEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgXminOrder : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public uint Version { get; set; }
}

public sealed class PgAuditEntry
{
    public Guid Id { get; set; }

    public string Message { get; set; } = string.Empty;
}
