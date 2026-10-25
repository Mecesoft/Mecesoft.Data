using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class TenantRelationshipSecurityTests
{
    [Fact]
    public void UnmarkedRelatedEntityCannotBeReadThroughDirectDbSet()
    {
        using var context = CreateContext<UnmarkedRelatedDbContext>(
            new UnmarkedRelatedDbContext(
                Options<UnmarkedRelatedDbContext>(),
                new TenantScopeStore(),
                [new TenantEntityMapping<UnmarkedOrder, Guid>(order => order.TenantId)],
                []));

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            nameof(UnmarkedNote),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeUnmarkedDependentFailsBeforeQueryExecution()
    {
        using var context = CreateContext<UnmarkedRelatedDbContext>(
            new UnmarkedRelatedDbContext(
                Options<UnmarkedRelatedDbContext>(),
                new TenantScopeStore(),
                [new TenantEntityMapping<UnmarkedOrder, Guid>(order => order.TenantId)],
                []));

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.Orders
                    .Include(order => order.Notes)
                    .ToList());

        Assert.Contains(
            nameof(UnmarkedNote),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnmarkedPrincipalCannotBeUsedAsCrossTenantRelationshipTarget()
    {
        using var context = CreateContext<UnmarkedPrincipalDbContext>(
            new UnmarkedPrincipalDbContext(
                Options<UnmarkedPrincipalDbContext>(),
                new TenantScopeStore(),
                [new TenantEntityMapping<UnmarkedChild, Guid>(child => child.TenantId)],
                []));

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            nameof(UnmarkedPrincipal),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTenantMappingFailsStartupValidation()
    {
        using var context = CreateContext<UnmappedTenantDbContext>(
            new UnmappedTenantDbContext(
                Options<UnmappedTenantDbContext>(),
                new TenantScopeStore(),
                [],
                []));

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            nameof(UnmarkedOrder),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedEntityRelationshipsDoNotRequireTenantMappings()
    {
        using var context = CreateContext<OwnedRelationshipDbContext>(
            new OwnedRelationshipDbContext(
                Options<OwnedRelationshipDbContext>(),
                new TenantScopeStore(),
                [new TenantEntityMapping<OwnedOrder, Guid>(order => order.TenantId)],
                []));

        context.ValidateTenantSecurity();
    }

    private static TContext CreateContext<TContext>(TContext context)
        where TContext : DbContext
    {
        return context;
    }

    private static DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
    }
}

public sealed class UnmarkedOrder : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<UnmarkedNote> Notes { get; } = [];
}

public sealed class UnmarkedNote
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid TenantId { get; set; }

    public string Text { get; set; } = string.Empty;

    public UnmarkedOrder? Order { get; set; }
}

public sealed class UnmarkedRelatedDbContext : MecesoftDbContext
{
    public UnmarkedRelatedDbContext(
        DbContextOptions<UnmarkedRelatedDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<UnmarkedOrder> Orders => Set<UnmarkedOrder>();

    public DbSet<UnmarkedNote> Notes => Set<UnmarkedNote>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnmarkedOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).IsRequired();
            entity.Property(order => order.Name).IsRequired();
            entity.HasMany(order => order.Notes)
                .WithOne(note => note.Order)
                .HasForeignKey(note => note.OrderId);
        });

        modelBuilder.Entity<UnmarkedNote>(entity =>
        {
            entity.HasKey(note => note.Id);
            entity.Property(note => note.TenantId).IsRequired();
            entity.Property(note => note.Text).IsRequired();
        });
    }
}

public sealed class UnmarkedChild : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid PrincipalId { get; set; }

    public UnmarkedPrincipal? Principal { get; set; }
}

public sealed class UnmarkedPrincipal
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<UnmarkedChild> Children { get; } = [];
}

public sealed class UnmarkedPrincipalDbContext : MecesoftDbContext
{
    public UnmarkedPrincipalDbContext(
        DbContextOptions<UnmarkedPrincipalDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<UnmarkedChild> Children => Set<UnmarkedChild>();

    public DbSet<UnmarkedPrincipal> Principals => Set<UnmarkedPrincipal>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnmarkedChild>(entity =>
        {
            entity.HasKey(child => child.Id);
            entity.Property(child => child.TenantId).IsRequired();
            entity.HasOne(child => child.Principal)
                .WithMany(principal => principal.Children)
                .HasForeignKey(child => child.PrincipalId);
        });

        modelBuilder.Entity<UnmarkedPrincipal>(entity =>
        {
            entity.HasKey(principal => principal.Id);
            entity.Property(principal => principal.Name).IsRequired();
        });
    }
}

public sealed class UnmappedTenantDbContext : MecesoftDbContext
{
    public UnmappedTenantDbContext(
        DbContextOptions<UnmappedTenantDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<UnmarkedOrder> Orders => Set<UnmarkedOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnmarkedOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).IsRequired();
            entity.Property(order => order.Name).IsRequired();
            entity.Ignore(order => order.Notes);
        });
    }
}

public sealed class OwnedOrder : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public OwnedOrderDetail? Detail { get; set; }
}

public sealed class OwnedOrderDetail
{
    public string Reference { get; set; } = string.Empty;
}

public sealed class OwnedRelationshipDbContext : MecesoftDbContext
{
    public OwnedRelationshipDbContext(
        DbContextOptions<OwnedRelationshipDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<OwnedOrder> Orders => Set<OwnedOrder>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnedOrder>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.TenantId).IsRequired();
            entity.Property(order => order.Name).IsRequired();
            entity.OwnsOne(order => order.Detail, detail =>
            {
                detail.Property(item => item.Reference).IsRequired();
            });
        });
    }
}
