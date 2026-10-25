using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class CascadeDeletePolicyTests
{
    [Fact]
    public void SoftDeletableChildCannotCascadeFromNonSoftDeletablePrincipal()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<UnsafeCascadeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new UnsafeCascadeDbContext(options, store, [], []);

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public void SoftDeletableChildCanUseRestrictFromNonSoftDeletablePrincipal()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<SafeCascadeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new SafeCascadeDbContext(options, store, [], []);

        Assert.NotNull(context.Model.FindEntityType(typeof(CascadeChild)));
    }
}

public sealed class CascadeParent
{
    public Guid Id { get; set; }
}

public sealed class CascadeChild : ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid ParentId { get; set; }

    public CascadeParent? Parent { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class UnsafeCascadeDbContext : MecesoftDbContext
{
    public UnsafeCascadeDbContext(
        DbContextOptions<UnsafeCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CascadeParent>(entity =>
        {
            entity.HasKey(parent => parent.Id);
        });

        modelBuilder.Entity<CascadeChild>(entity =>
        {
            entity.HasKey(child => child.Id);
            entity.HasOne(child => child.Parent)
                .WithMany()
                .HasForeignKey(child => child.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class SafeCascadeDbContext : MecesoftDbContext
{
    public SafeCascadeDbContext(
        DbContextOptions<SafeCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CascadeParent>(entity =>
        {
            entity.HasKey(parent => parent.Id);
        });

        modelBuilder.Entity<CascadeChild>(entity =>
        {
            entity.HasKey(child => child.Id);
            entity.HasOne(child => child.Parent)
                .WithMany()
                .HasForeignKey(child => child.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
