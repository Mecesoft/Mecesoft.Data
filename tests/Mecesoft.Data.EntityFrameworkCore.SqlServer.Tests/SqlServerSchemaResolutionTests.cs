using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerSchemaResolutionTests
{
    [SqlServerFact]
    public async Task DefaultSchemaValidationDoesNotFallBackToDbo()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await database.ExecuteAsync(
                """
                CREATE TABLE dbo.TenantEntities (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    tenant_id nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    Name nvarchar(256) NOT NULL,
                    Version bigint NOT NULL);
                """);
            await database.ExecuteAsync(
                """
                CREATE TABLE custom.TenantEntities (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    tenant_id varchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    Name nvarchar(256) NOT NULL,
                    Version bigint NOT NULL);
                """);

            using var context = new SqlServerSchemaResolutionDbContext(
                database.OptionsWithProductionTenancy<
                    SqlServerSchemaResolutionDbContext>(),
                new TenantScopeStore(),
                [new TenantEntityMapping<SqlStringTenantEntity, string>(
                    entity => entity.TenantId)],
                []);

            TenantConfigurationException exception =
                Assert.Throws<TenantConfigurationException>(
                    () => context.ValidateTenantSecurity());

            Assert.Contains(
                "unsupported SQL Server type",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await database.DisposeAsync<SqlServerSchemaResolutionDbContext>(
                options => new SqlServerSchemaResolutionDbContext(
                    options,
                    new TenantScopeStore(),
                    [new TenantEntityMapping<SqlStringTenantEntity, string>(
                        entity => entity.TenantId)],
                    []));
        }
    }
}

public sealed class SqlServerSchemaResolutionDbContext : MecesoftDbContext
{
    public SqlServerSchemaResolutionDbContext(
        DbContextOptions<SqlServerSchemaResolutionDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlStringTenantEntity> StringEntities =>
        Set<SqlStringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqlServerTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("custom");
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
    }
}
