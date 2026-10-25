using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class GlobalEntityPolicyTests
{
    [Fact]
    public async Task TenantEntityCanReferenceExplicitGlobalLookup()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var mapping = new TenantEntityMapping<GlobalOrder, Guid>(order => order.TenantId);
        await using var context = new GlobalLookupDbContext(
            database.Options<GlobalLookupDbContext>(),
            store,
            [mapping],
            []);
        await context.Database.EnsureCreatedAsync();

        var country = new GlobalCountry
        {
            Id = Guid.NewGuid(),
            Name = "Global",
        };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            country,
            TenantOperation.Create);
        context.Countries.Add(country);

        // Explicit global entities are shared; they do not require a tenant scope.
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Guid tenantId = Guid.NewGuid();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Orders.Add(new GlobalOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CountryId = country.Id,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await context.Orders.AsNoTracking().ToListAsync());
        Assert.Single(await context.Countries.AsNoTracking().ToListAsync());

        Guid otherTenant = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(otherTenant, TenantOperation.Read));
        context.AuthorizeReadForTesting(otherTenant);
        Assert.Empty(await context.Orders.AsNoTracking().ToListAsync());
        Assert.Single(await context.Countries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void GlobalEntityCannotReferenceTenantEntity()
    {
        var store = new TenantScopeStore();
        var mapping = new TenantEntityMapping<GlobalOrder, Guid>(order => order.TenantId);
        var options = new DbContextOptionsBuilder<InvalidGlobalReferenceDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new InvalidGlobalReferenceDbContext(
            options,
            store,
            [mapping],
            []);

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public void GlobalEntityCannotHaveUnmarkedDependent()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<UnmarkedGlobalDependentDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new UnmarkedGlobalDependentDbContext(options, store, [], []);

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public void GlobalEntityCannotAlsoHaveTenantMapping()
    {
        var store = new TenantScopeStore();
        var mapping = new TenantEntityMapping<ConflictingGlobalEntity, Guid>(entity => entity.TenantId);
        var options = new DbContextOptionsBuilder<ConflictingGlobalDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new ConflictingGlobalDbContext(
            options,
            store,
            [mapping],
            []);

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }
}

public sealed class GlobalCountry
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class GlobalOrder : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? CountryId { get; set; }

    public GlobalCountry? Country { get; set; }
}

public sealed class GlobalLookupDbContext : MecesoftDbContext
{
    public GlobalLookupDbContext(
        DbContextOptions<GlobalLookupDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<GlobalCountry> Countries => Set<GlobalCountry>();

    public DbSet<GlobalOrder> Orders => Set<GlobalOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalCountry>(entity =>
        {
            entity.HasKey(country => country.Id);
            entity.Property(country => country.Name).IsRequired();
            entity.IsGlobal();
        });

        modelBuilder.Entity<GlobalOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).IsRequired();
            entity.HasOne(order => order.Country)
                .WithMany()
                .HasForeignKey(order => order.CountryId)
                .IsRequired(false);
        });
    }
}

public sealed class InvalidGlobalCountry
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid? OrderId { get; set; }

    public GlobalOrder? Order { get; set; }
}

public sealed class InvalidGlobalReferenceDbContext : MecesoftDbContext
{
    public InvalidGlobalReferenceDbContext(
        DbContextOptions<InvalidGlobalReferenceDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<InvalidGlobalCountry> Countries => Set<InvalidGlobalCountry>();

    public DbSet<GlobalOrder> Orders => Set<GlobalOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).IsRequired();
            entity.Ignore(order => order.Country);
        });

        modelBuilder.Entity<InvalidGlobalCountry>(entity =>
        {
            entity.HasKey(country => country.Id);
            entity.Property(country => country.Name).IsRequired();
            entity.HasOne(country => country.Order)
                .WithMany()
                .HasForeignKey(country => country.OrderId)
                .IsRequired(false);
            entity.IsGlobal();
        });
    }
}

public sealed class UnmarkedCity
{
    public Guid Id { get; set; }

    public Guid CountryId { get; set; }

    public GlobalCountry? Country { get; set; }
}

public sealed class UnmarkedGlobalDependentDbContext : MecesoftDbContext
{
    public UnmarkedGlobalDependentDbContext(
        DbContextOptions<UnmarkedGlobalDependentDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<GlobalCountry> Countries => Set<GlobalCountry>();

    public DbSet<UnmarkedCity> Cities => Set<UnmarkedCity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalCountry>(entity =>
        {
            entity.HasKey(country => country.Id);
            entity.Property(country => country.Name).IsRequired();
            entity.IsGlobal();
        });

        modelBuilder.Entity<UnmarkedCity>(entity =>
        {
            entity.HasKey(city => city.Id);
            entity.HasOne(city => city.Country)
                .WithMany()
                .HasForeignKey(city => city.CountryId);
        });
    }
}

public sealed class ConflictingGlobalEntity : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }
}

public sealed class ConflictingGlobalDbContext : MecesoftDbContext
{
    public ConflictingGlobalDbContext(
        DbContextOptions<ConflictingGlobalDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<ConflictingGlobalEntity> Entities => Set<ConflictingGlobalEntity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConflictingGlobalEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.IsGlobal();
        });
    }
}
