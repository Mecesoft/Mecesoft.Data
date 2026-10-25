using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresTestDbContext : MecesoftDbContext
{
    public PostgresTestDbContext(
        DbContextOptions<PostgresTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgOrder> Orders => Set<PgOrder>();

    public DbSet<PgOrderLine> OrderLines => Set<PgOrderLine>();

    public DbSet<PgAuditEntry> AuditEntries => Set<PgAuditEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Name).IsRequired();
            entity.Property(order => order.CreatedAt).HasColumnType("timestamp with time zone");
            entity.Property(order => order.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            entity.HasMany(order => order.Lines)
                .WithOne(line => line.Order)
                .HasForeignKey(line => line.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PgOrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.Property(line => line.TenantId).IsRequired();
        });

        modelBuilder.Entity<PgAuditEntry>(entity =>
        {
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Message).IsRequired();
        });
    }
}

public sealed class PostgresXminTestDbContext : MecesoftDbContext
{
    public PostgresXminTestDbContext(
        DbContextOptions<PostgresXminTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgXminOrder> Orders => Set<PgXminOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgXminOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Name).IsRequired();
            entity.Property(order => order.Version).IsRowVersion();
        });
    }
}

internal static class PostgresTestMappings
{
    public static ITenantEntityMapping[] TenantMappings() =>
    [
        new TenantEntityMapping<PgOrder, Guid>(order => order.TenantId),
        new TenantEntityMapping<PgOrderLine, Guid>(line => line.TenantId),
    ];

    public static ITenantEntityMapping[] XminTenantMappings() =>
    [
        new TenantEntityMapping<PgXminOrder, Guid>(order => order.TenantId),
    ];
}
