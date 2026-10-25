using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerBin2StringDbContext : MecesoftDbContext
{
    public SqlServerBin2StringDbContext(
        DbContextOptions<SqlServerBin2StringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    public DbSet<SqlStringSoftDeleteEntity> SoftDeleteEntities =>
        Set<SqlStringSoftDeleteEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.ToTable("TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });

        modelBuilder.Entity<SqlStringSoftDeleteEntity>(entity =>
        {
            entity.ToTable("SoftDeleteTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerQuotedSchemaStringDbContext : MecesoftDbContext
{
    public SqlServerQuotedSchemaStringDbContext(
        DbContextOptions<SqlServerQuotedSchemaStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.ToTable("Tenant Orders", "tenancy");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("Tenant Id")
                .IsRequired()
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name)
                .HasColumnName("Display Name")
                .IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerBin2Utf8StringDbContext : MecesoftDbContext
{
    public SqlServerBin2Utf8StringDbContext(
        DbContextOptions<SqlServerBin2Utf8StringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.ToTable("Utf8TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .HasColumnType("varchar(512)")
                .IsRequired()
                .UseCollation("Latin1_General_100_BIN2_UTF8");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerCustomMappingStringDbContext : MecesoftDbContext
{
    public SqlServerCustomMappingStringDbContext(
        DbContextOptions<SqlServerCustomMappingStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.ToTable("CustomTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerCharStringDbContext : MecesoftDbContext
{    public SqlServerCharStringDbContext(
        DbContextOptions<SqlServerCharStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.ToTable("CharTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .HasColumnType("char(36)")
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerEncodingUnsafeStringDbContext : MecesoftDbContext
{
    public SqlServerEncodingUnsafeStringDbContext(
        DbContextOptions<SqlServerEncodingUnsafeStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringVarcharNonUtf8Entity> NonUtf8Entities =>
        Set<SqlStringVarcharNonUtf8Entity>();

    public DbSet<SqlStringNarrowUtf8Entity> NarrowUtf8Entities =>
        Set<SqlStringNarrowUtf8Entity>();

    public DbSet<SqlStringNarrowNvarcharEntity> NarrowNvarcharEntities =>
        Set<SqlStringNarrowNvarcharEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringVarcharNonUtf8Entity>(entity =>
        {
            entity.ToTable("NonUtf8TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .HasColumnType("varchar(64)")
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<SqlStringNarrowUtf8Entity>(entity =>
        {
            entity.ToTable("NarrowUtf8TenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .HasColumnType("varchar(256)")
                .UseCollation("Latin1_General_100_BIN2_UTF8");
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<SqlStringNarrowNvarcharEntity>(entity =>
        {
            entity.ToTable("NarrowNvarcharTenantEntities");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .HasColumnType("nvarchar(64)")
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class SqlStringVarcharNonUtf8Entity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class SqlStringNarrowUtf8Entity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class SqlStringNarrowNvarcharEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

internal static class SqlServerProductionStringMappings
{
    public static ITenantEntityMapping[] TenantMappings() =>
    [
        new TenantEntityMapping<SqlStringTenantEntity, string>(item => item.TenantId),
    ];

    public static ITenantEntityMapping[] Bin2TenantMappings() =>
    [
        new TenantEntityMapping<SqlStringTenantEntity, string>(item => item.TenantId),
        new TenantEntityMapping<SqlStringSoftDeleteEntity, string>(item => item.TenantId),
    ];

    public static ITenantEntityMapping[] UnsafeEncodingMappings() =>
    [
        new TenantEntityMapping<SqlStringVarcharNonUtf8Entity, string>(
            item => item.TenantId),
        new TenantEntityMapping<SqlStringNarrowUtf8Entity, string>(
            item => item.TenantId),
        new TenantEntityMapping<SqlStringNarrowNvarcharEntity, string>(
            item => item.TenantId),
    ];
}
