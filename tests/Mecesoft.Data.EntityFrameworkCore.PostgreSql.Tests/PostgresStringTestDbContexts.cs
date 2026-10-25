using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresStringTestDbContext : MecesoftDbContext
{
    public PostgresStringTestDbContext(
        DbContextOptions<PostgresStringTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgStringTenantEntity> StringEntities => Set<PgStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => PostgresColumnCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgStringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("text");
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class PostgresCaseInsensitiveStringTestDbContext : MecesoftDbContext
{
    public const string CaseInsensitiveCollation = "mecesoft_ci";

    public PostgresCaseInsensitiveStringTestDbContext(
        DbContextOptions<PostgresCaseInsensitiveStringTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgStringTenantEntity> StringEntities => Set<PgStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => PostgresColumnCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PgStringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId)
                .IsRequired()
                .HasColumnType("text")
                .UseCollation(CaseInsensitiveCollation);
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

internal static class PostgresStringTestMappings
{
    public static ITenantEntityMapping[] TenantMappings() =>
    [
        new TenantEntityMapping<PgStringTenantEntity, string>(item => item.TenantId),
    ];
}
