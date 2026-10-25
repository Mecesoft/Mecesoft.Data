using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerTestDbContext : MecesoftDbContext
{
    public SqlServerTestDbContext(
        DbContextOptions<SqlServerTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlOrder> Orders => Set<SqlOrder>();

    public DbSet<SqlOrderLine> OrderLines => Set<SqlOrderLine>();

    public DbSet<SqlAuditEntry> AuditEntries => Set<SqlAuditEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Name).IsRequired();
            entity.Property(order => order.CreatedAt).HasColumnType("datetimeoffset");
            entity.Property(order => order.Version).IsRowVersion();
            entity.HasMany(order => order.Lines)
                .WithOne(line => line.Order)
                .HasForeignKey(line => line.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SqlOrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.Property(line => line.TenantId).IsRequired();
        });

        modelBuilder.Entity<SqlAuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Message).IsRequired();
        });
    }
}

internal static class SqlServerTestMappings
{
    public static ITenantEntityMapping[] TenantMappings() =>
    [
        new TenantEntityMapping<SqlOrder, Guid>(order => order.TenantId),
        new TenantEntityMapping<SqlOrderLine, Guid>(line => line.TenantId),
    ];
}
