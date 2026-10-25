using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class OwnedSoftDeletePolicyTests
{
    [Fact]
    public void OwnedSoftDeletableEntityIsRejectedAtModelCreation()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<OwnedSoftDeleteDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new OwnedSoftDeleteDbContext(
            options,
            store,
            [],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "owned",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "ISoftDeletable",
            exception.Message,
            StringComparison.Ordinal);
    }
}

public sealed class OwnedSoftDeleteOwner
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public OwnedSoftDeleteDetail? Detail { get; set; }
}

public sealed class OwnedSoftDeleteDetail : ISoftDeletable
{
    public string Value { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class OwnedSoftDeleteDbContext : MecesoftDbContext
{
    public OwnedSoftDeleteDbContext(
        DbContextOptions<OwnedSoftDeleteDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<OwnedSoftDeleteOwner> Owners => Set<OwnedSoftDeleteOwner>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnedSoftDeleteOwner>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.OwnsOne(item => item.Detail, detail =>
            {
                detail.Property(item => item.Value).IsRequired();
            });
        });
    }
}
