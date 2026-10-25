using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PgProductionTextEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionVarcharEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionQuotedEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionCitextEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionBpcharEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionDomainEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionCiEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionNarrowVarcharEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class PgProductionOrder : ITenantEntity<string>, ISoftDeletable
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid Version { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class PostgresSafeProductionDbContext : MecesoftDbContext
{
    public PostgresSafeProductionDbContext(
        DbContextOptions<PostgresSafeProductionDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgProductionTextEntity> TextEntities => Set<PgProductionTextEntity>();

    public DbSet<PgProductionVarcharEntity> VarcharEntities =>
        Set<PgProductionVarcharEntity>();

    public DbSet<PgProductionQuotedEntity> QuotedEntities =>
        Set<PgProductionQuotedEntity>();

    public DbSet<PgProductionOrder> Orders => Set<PgProductionOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgProductionTextEntity>(entity =>
        {
            entity.ToTable("TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired().HasColumnType("text");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionOrder>(entity =>
        {
            entity.ToTable("ProductionOrders");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired().HasColumnType("text");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });

        modelBuilder.Entity<PgProductionVarcharEntity>(entity =>
        {
            entity.ToTable("VarcharTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("varchar(128)");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionQuotedEntity>(entity =>
        {
            entity.ToTable("Tenant Orders", "tenancy");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("Tenant Id")
                .IsRequired()
                .HasColumnType("text");
            entity.Property(item => item.Name)
                .HasColumnName("Display Name")
                .IsRequired();
        });
    }
}

public sealed class PostgresUnsafeProductionDbContext : MecesoftDbContext
{
    public PostgresUnsafeProductionDbContext(
        DbContextOptions<PostgresUnsafeProductionDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgProductionCitextEntity> CitextEntities =>
        Set<PgProductionCitextEntity>();

    public DbSet<PgProductionBpcharEntity> BpcharEntities =>
        Set<PgProductionBpcharEntity>();

    public DbSet<PgProductionDomainEntity> DomainEntities =>
        Set<PgProductionDomainEntity>();

    public DbSet<PgProductionCiEntity> CiEntities => Set<PgProductionCiEntity>();

    public DbSet<PgProductionNarrowVarcharEntity> NarrowVarcharEntities =>
        Set<PgProductionNarrowVarcharEntity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgProductionCitextEntity>(entity =>
        {
            entity.ToTable("CitextTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired().HasColumnType("citext");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionBpcharEntity>(entity =>
        {
            entity.ToTable("BpcharTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("bpchar(16)");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionDomainEntity>(entity =>
        {
            entity.ToTable("DomainTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("tenant_domain");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionCiEntity>(entity =>
        {
            entity.ToTable("CiTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("text")
                .UseCollation("mecesoft_ci");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<PgProductionNarrowVarcharEntity>(entity =>
        {
            entity.ToTable("NarrowVarcharTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("varchar(64)");
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class PostgresPropertyOnlyProductionDbContext : MecesoftDbContext
{
    public PostgresPropertyOnlyProductionDbContext(
        DbContextOptions<PostgresPropertyOnlyProductionDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgProductionTextEntity> TextEntities => Set<PgProductionTextEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => PostgreSqlTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgProductionTextEntity>(entity =>
        {
            entity.ToTable("TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired().HasColumnType("text");
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

internal static class PostgresProductionTestMappings
{
    public static ITenantEntityMapping[] SafeMappings() =>
    [
        new TenantEntityMapping<PgProductionTextEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionVarcharEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionQuotedEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionOrder, string>(item => item.TenantId),
    ];

    public static ISoftDeleteEntityMapping[] SoftDeleteMappings() =>
    [
        new SoftDeleteEntityMapping<PgProductionOrder>(),
    ];

    public static ITenantEntityMapping[] UnsafeMappings() =>
    [
        new TenantEntityMapping<PgProductionCitextEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionBpcharEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionDomainEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionCiEntity, string>(item => item.TenantId),
        new TenantEntityMapping<PgProductionNarrowVarcharEntity, string>(item => item.TenantId),
    ];
}
