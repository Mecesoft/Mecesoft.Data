using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerCaseInsensitiveStringDbContext : MecesoftDbContext
{
    public SqlServerCaseInsensitiveStringDbContext(
        DbContextOptions<SqlServerCaseInsensitiveStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerColumnCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

public sealed class SqlServerCaseSensitiveStringDbContext : MecesoftDbContext
{
    public SqlServerCaseSensitiveStringDbContext(
        DbContextOptions<SqlServerCaseSensitiveStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities => Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerColumnCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlStringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .UseCollation("Latin1_General_100_CS_AS");
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}

internal static class SqlServerStringTestMappings
{
    public static ITenantEntityMapping[] TenantMappings() =>
    [
        new TenantEntityMapping<SqlStringTenantEntity, string>(item => item.TenantId),
    ];
}
