using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlOrder : ITenantEntity<Guid>, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public byte[] Version { get; set; } = [];

    public List<SqlOrderLine> Lines { get; } = [];
}

public sealed class SqlOrderLine : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid TenantId { get; set; }

    public SqlOrder? Order { get; set; }
}

public sealed class SqlStringTenantEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long Version { get; set; } = 1;
}

public sealed class SqlStringSoftDeleteEntity : ITenantEntity<string>, ISoftDeletable
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public long Version { get; set; } = 1;
}

public sealed class SqlAuditEntry
{
    public Guid Id { get; set; }

    public string Message { get; set; } = string.Empty;
}
