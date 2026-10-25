using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerRelationshipVerifierTests
{
    [SqlServerFact]
    public async Task SafeRestrictedGlobalTenantForeignKeyPasses()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await using SqlServerRelationshipDbContext context =
                CreateContext(database);
            await context.Database.EnsureCreatedAsync();

            context.ValidateTenantSecurity();
        }
        finally
        {
            await database.DisposeAsync<SqlServerRelationshipDbContext>(
                options => CreateContext(database, options));
        }
    }

    [SqlServerFact]
    public async Task UnmodeledCascadeOnGlobalTenantForeignKeyFailsClosed()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await using (SqlServerRelationshipDbContext context =
                CreateContext(database))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await database.ExecuteAsync(
                """
                ALTER TABLE [custom].[TenantDependent]
                    DROP CONSTRAINT [FK_TenantDependent_GlobalLookup];
                ALTER TABLE [custom].[TenantDependent]
                    ADD CONSTRAINT [FK_TenantDependent_GlobalLookup]
                    FOREIGN KEY ([GlobalId])
                    REFERENCES [custom].[GlobalLookup] ([Id])
                    ON DELETE CASCADE;
                """);

            await using SqlServerRelationshipDbContext driftContext =
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
        finally
        {
            await database.DisposeAsync<SqlServerRelationshipDbContext>(
                options => CreateContext(database, options));
        }
    }

    [SqlServerFact]
    public async Task MissingForeignKeyFailsClosed()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await using (SqlServerRelationshipDbContext context =
                CreateContext(database))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await database.ExecuteAsync(
                """
                ALTER TABLE [custom].[TenantDependent]
                    DROP CONSTRAINT [FK_TenantDependent_GlobalLookup];
                """);

            await using SqlServerRelationshipDbContext missingContext =
                CreateContext(database);
            TenantConfigurationException exception =
                Assert.Throws<TenantConfigurationException>(
                    () => missingContext.ValidateTenantSecurity());

            Assert.Contains(
                "was not found",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await database.DisposeAsync<SqlServerRelationshipDbContext>(
                options => CreateContext(database, options));
        }
    }

    [SqlServerFact]
    public async Task UnmodeledExtraForeignKeyFailsClosed()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await using (SqlServerRelationshipDbContext context =
                CreateContext(database))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await database.ExecuteAsync(
                """
                ALTER TABLE [custom].[TenantDependent]
                    ADD [OtherGlobalId] uniqueidentifier NULL;
                ALTER TABLE [custom].[TenantDependent]
                    ADD CONSTRAINT [FK_TenantDependent_OtherGlobal]
                    FOREIGN KEY ([OtherGlobalId])
                    REFERENCES [custom].[GlobalLookup] ([Id])
                    ON DELETE CASCADE;
                """);

            await using SqlServerRelationshipDbContext extraContext =
                CreateContext(database);
            TenantConfigurationException exception =
                Assert.Throws<TenantConfigurationException>(
                    () => extraContext.ValidateTenantSecurity());

            Assert.Contains(
                "not represented",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await database.DisposeAsync<SqlServerRelationshipDbContext>(
                options => CreateContext(database, options));
        }
    }

    [SqlServerFact]
    public async Task AmbiguousForeignKeyMetadataFailsClosed()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.CreateDatabaseAsync();
            await database.ExecuteAsync("CREATE SCHEMA custom;");
            await using (SqlServerRelationshipDbContext context =
                CreateContext(database))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await database.ExecuteAsync(
                """
                ALTER TABLE [custom].[TenantDependent]
                    DROP CONSTRAINT [FK_TenantDependent_GlobalLookup];
                ALTER TABLE [custom].[TenantDependent]
                    ADD CONSTRAINT [FK_TenantDependent_GlobalLookup_A]
                    FOREIGN KEY ([GlobalId])
                    REFERENCES [custom].[GlobalLookup] ([Id])
                    ON DELETE NO ACTION;
                ALTER TABLE [custom].[TenantDependent]
                    ADD CONSTRAINT [FK_TenantDependent_GlobalLookup_B]
                    FOREIGN KEY ([GlobalId])
                    REFERENCES [custom].[GlobalLookup] ([Id])
                    ON DELETE NO ACTION;
                """);

            await using SqlServerRelationshipDbContext ambiguousContext =
                CreateContext(database);
            TenantConfigurationException exception =
                Assert.Throws<TenantConfigurationException>(
                    () => ambiguousContext.ValidateTenantSecurity());

            Assert.Contains(
                "ambiguous",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await database.DisposeAsync<SqlServerRelationshipDbContext>(
                options => CreateContext(database, options));
        }
    }

    private static SqlServerRelationshipDbContext CreateContext(
        SqlServerTestDatabase database,
        DbContextOptions<SqlServerRelationshipDbContext>? options = null)
    {
        return new SqlServerRelationshipDbContext(
            options ?? database.OptionsWithProductionTenancy<SqlServerRelationshipDbContext>(),
            new TenantScopeStore(),
            [
                new TenantEntityMapping<SqlRelationshipTenant, Guid>(
                    entity => entity.TenantId),
            ],
            []);
    }
}

public sealed class SqlRelationshipGlobal
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class SqlRelationshipTenant : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GlobalId { get; set; }

    public SqlRelationshipGlobal? Global { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class SqlServerRelationshipDbContext : MecesoftDbContext
{
    public SqlServerRelationshipDbContext(
        DbContextOptions<SqlServerRelationshipDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SqlRelationshipGlobal> Globals => Set<SqlRelationshipGlobal>();

    public DbSet<SqlRelationshipTenant> Tenants => Set<SqlRelationshipTenant>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("custom");

        modelBuilder.Entity<SqlRelationshipGlobal>(entity =>
        {
            entity.ToTable("GlobalLookup", "custom");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.IsGlobal();
        });

        modelBuilder.Entity<SqlRelationshipTenant>(entity =>
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
