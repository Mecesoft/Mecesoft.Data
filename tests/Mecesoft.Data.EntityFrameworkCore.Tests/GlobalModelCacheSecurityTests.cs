using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class GlobalModelCacheSecurityTests
{
    [Fact]
    public async Task DifferentGlobalDeclarationsDoNotShareCachedModel()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<ConditionalGlobalDbContext> options =
            database.Options<ConditionalGlobalDbContext>();

        await using var global = new ConditionalGlobalDbContext(
            options,
            new TenantScopeStore(),
            markGlobal: true);
        await global.Database.EnsureCreatedAsync();
        Assert.True(IsGlobal(global.Model));

        await using var nonGlobal = new ConditionalGlobalDbContext(
            options,
            new TenantScopeStore(),
            markGlobal: false);

        Assert.NotSame(global.Model, nonGlobal.Model);
        Assert.True(IsGlobal(global.Model));
        Assert.False(IsGlobal(nonGlobal.Model));
    }

    [Fact]
    public async Task UnsafeGlobalDeclarationCannotReuseSafeCachedModel()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<ConditionalGlobalSecurityDbContext> options =
            database.Options<ConditionalGlobalSecurityDbContext>();
        var mapping = new TenantEntityMapping<ConditionalSecurityOwner, Guid>(
            owner => owner.TenantId);

        await using var safe = new ConditionalGlobalSecurityDbContext(
            options,
            new TenantScopeStore(),
            [mapping],
            markGlobal: true);
        await safe.Database.EnsureCreatedAsync();
        _ = safe.Model;

        await using var unsafeContext = new ConditionalGlobalSecurityDbContext(
            options,
            new TenantScopeStore(),
            [mapping],
            markGlobal: false);

        Assert.Throws<TenantConfigurationException>(() => _ = unsafeContext.Model);
    }

    [Fact]
    public async Task SameGlobalDeclarationCanReuseCachedModel()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<ConditionalGlobalDbContext> options =
            database.Options<ConditionalGlobalDbContext>();

        await using var first = new ConditionalGlobalDbContext(
            options,
            new TenantScopeStore(),
            markGlobal: true);
        await first.Database.EnsureCreatedAsync();
        _ = first.Model;

        await using var second = new ConditionalGlobalDbContext(
            options,
            new TenantScopeStore(),
            markGlobal: true);

        Assert.Same(first.Model, second.Model);
    }

    private static bool IsGlobal(IModel model)
    {
        return model.FindEntityType(typeof(ConditionalGlobalEntity))
            ?.FindAnnotation(GlobalEntityModelBuilderExtensions.AnnotationName)
            ?.Value is true;
    }
}

public sealed class ConditionalGlobalEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class ConditionalGlobalDbContext : MecesoftDbContext
{
    private readonly bool _markGlobal;

    public ConditionalGlobalDbContext(
        DbContextOptions<ConditionalGlobalDbContext> options,
        ITenantScopeStore tenantScope,
        bool markGlobal)
        : base(options, tenantScope, [], [])
    {
        _markGlobal = markGlobal;
    }

    public DbSet<ConditionalGlobalEntity> Entities => Set<ConditionalGlobalEntity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConditionalGlobalEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            if (_markGlobal)
            {
                entity.IsGlobal();
            }
        });
    }
}


public sealed class ConditionalSecurityLookup
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class ConditionalSecurityOwner : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid LookupId { get; set; }

    public ConditionalSecurityLookup? Lookup { get; set; }
}

public sealed class ConditionalGlobalSecurityDbContext : MecesoftDbContext
{
    private readonly bool _markGlobal;

    public ConditionalGlobalSecurityDbContext(
        DbContextOptions<ConditionalGlobalSecurityDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        bool markGlobal)
        : base(options, tenantScope, tenantMappings, [])
    {
        _markGlobal = markGlobal;
    }

    public DbSet<ConditionalSecurityLookup> Lookups =>
        Set<ConditionalSecurityLookup>();

    public DbSet<ConditionalSecurityOwner> Owners =>
        Set<ConditionalSecurityOwner>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConditionalSecurityLookup>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            if (_markGlobal)
            {
                entity.IsGlobal();
            }
        });

        modelBuilder.Entity<ConditionalSecurityOwner>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.HasOne(item => item.Lookup)
                .WithMany()
                .HasForeignKey(item => item.LookupId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
