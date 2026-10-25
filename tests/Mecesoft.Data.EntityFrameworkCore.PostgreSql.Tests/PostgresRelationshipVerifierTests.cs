using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresRelationshipVerifierTests
{
    [PostgresFact]
    public async Task SafeRestrictedGlobalTenantForeignKeyPasses()
    {
        await using PostgresTestDatabase database =
            await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA custom;");
        await using PostgresRelationshipDbContext context =
            CreateContext(database);
        await context.Database.EnsureCreatedAsync();

        context.ValidateTenantSecurity();
    }

    [PostgresFact]
    public async Task UnmodeledCascadeOnGlobalTenantForeignKeyFailsClosed()
    {
        await using PostgresTestDatabase database =
            await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA custom;");
        await using (PostgresRelationshipDbContext context =
            CreateContext(database))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await database.ExecuteAsync(
            """
            ALTER TABLE "custom"."TenantDependent"
                DROP CONSTRAINT "FK_TenantDependent_GlobalLookup";
            ALTER TABLE "custom"."TenantDependent"
                ADD CONSTRAINT "FK_TenantDependent_GlobalLookup"
                FOREIGN KEY ("GlobalId")
                REFERENCES "custom"."GlobalLookup" ("Id")
                ON DELETE CASCADE;
            """);

        await using PostgresRelationshipDbContext driftContext =
            CreateContext(database);
        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => driftContext.ValidateTenantSecurity());

        Assert.Contains(
            "foreign key",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "cascade",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task MissingForeignKeyFailsClosed()
    {
        await using PostgresTestDatabase database =
            await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA custom;");
        await using (PostgresRelationshipDbContext context =
            CreateContext(database))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await database.ExecuteAsync(
            """
            ALTER TABLE "custom"."TenantDependent"
                DROP CONSTRAINT "FK_TenantDependent_GlobalLookup";
            """);

        await using PostgresRelationshipDbContext missingContext =
            CreateContext(database);
        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => missingContext.ValidateTenantSecurity());

        Assert.Contains(
            "was not found",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task UnmodeledExtraForeignKeyFailsClosed()
    {
        await using PostgresTestDatabase database =
            await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA custom;");
        await using (PostgresRelationshipDbContext context =
            CreateContext(database))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await database.ExecuteAsync(
            """
            ALTER TABLE "custom"."TenantDependent"
                ADD "OtherGlobalId" uuid NULL;
            ALTER TABLE "custom"."TenantDependent"
                ADD CONSTRAINT "FK_TenantDependent_OtherGlobal"
                FOREIGN KEY ("OtherGlobalId")
                REFERENCES "custom"."GlobalLookup" ("Id")
                ON DELETE CASCADE;
            """);

        await using PostgresRelationshipDbContext extraContext =
            CreateContext(database);
        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => extraContext.ValidateTenantSecurity());

        Assert.Contains(
            "not represented",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task AmbiguousForeignKeyMetadataFailsClosed()
    {
        await using PostgresTestDatabase database =
            await PostgresTestDatabase.CreateAsync();
        await database.ExecuteAsync("CREATE SCHEMA custom;");
        await using (PostgresRelationshipDbContext context =
            CreateContext(database))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await database.ExecuteAsync(
            """
            ALTER TABLE "custom"."TenantDependent"
                DROP CONSTRAINT "FK_TenantDependent_GlobalLookup";
            ALTER TABLE "custom"."TenantDependent"
                ADD CONSTRAINT "FK_TenantDependent_GlobalLookup_A"
                FOREIGN KEY ("GlobalId")
                REFERENCES "custom"."GlobalLookup" ("Id")
                ON DELETE NO ACTION;
            ALTER TABLE "custom"."TenantDependent"
                ADD CONSTRAINT "FK_TenantDependent_GlobalLookup_B"
                FOREIGN KEY ("GlobalId")
                REFERENCES "custom"."GlobalLookup" ("Id")
                ON DELETE NO ACTION;
            """);

        await using PostgresRelationshipDbContext ambiguousContext =
            CreateContext(database);
        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => ambiguousContext.ValidateTenantSecurity());

        Assert.Contains(
            "ambiguous",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static PostgresRelationshipDbContext CreateContext(
        PostgresTestDatabase database)
    {
        return new PostgresRelationshipDbContext(
            database.OptionsWithProductionTenancy<PostgresRelationshipDbContext>(),
            new TenantScopeStore(),
            [
                new TenantEntityMapping<PgRelationshipTenant, Guid>(
                    entity => entity.TenantId),
            ],
            []);
    }
}

public sealed class PgRelationshipGlobal
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class PgRelationshipTenant : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GlobalId { get; set; }

    public PgRelationshipGlobal? Global { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class PostgresRelationshipDbContext : MecesoftDbContext
{
    public PostgresRelationshipDbContext(
        DbContextOptions<PostgresRelationshipDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<PgRelationshipGlobal> Globals => Set<PgRelationshipGlobal>();

    public DbSet<PgRelationshipTenant> Tenants => Set<PgRelationshipTenant>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("custom");

        modelBuilder.Entity<PgRelationshipGlobal>(entity =>
        {
            entity.ToTable("GlobalLookup", "custom");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.IsGlobal();
        });

        modelBuilder.Entity<PgRelationshipTenant>(entity =>
        {
            entity.ToTable("TenantDependent", "custom");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.Property(item => item.Name).IsRequired();
            entity.HasOne(item => item.Global)
                .WithMany()
                .HasForeignKey(item => item.GlobalId)
                .HasConstraintName("FK_TenantDependent_GlobalLookup")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
