using Mecesoft.Data.Abstractions.Auditing;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class LegacyProduct
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public long Version { get; set; } = 1;

    public List<LegacyProductTag> Tags { get; } = [];
}

public sealed class LegacyProductTag
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public string Label { get; set; } = string.Empty;

    public LegacyProduct? Product { get; set; }

    public List<LegacyProductTagDetail> Details { get; } = [];
}

public sealed class LegacyProductTagDetail
{
    public Guid Id { get; set; }

    public Guid TagId { get; set; }

    public string Note { get; set; } = string.Empty;

    public LegacyProductTag? Tag { get; set; }
}

public sealed class LegacySoftEntity : ISoftDeletable
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class LegacyStringEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class LegacyAuditedEntity : IAuditable
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}

public class LegacyDbContext : DbContext
{
    public LegacyDbContext(DbContextOptions<LegacyDbContext> options)
        : base(options)
    {
    }

    public DbSet<LegacyProduct> Products => Set<LegacyProduct>();

    public DbSet<LegacyProductTag> ProductTags => Set<LegacyProductTag>();

    public DbSet<LegacyProductTagDetail> ProductTagDetails => Set<LegacyProductTagDetail>();

    public DbSet<LegacySoftEntity> SoftEntities => Set<LegacySoftEntity>();

    public DbSet<LegacyStringEntity> StringEntities => Set<LegacyStringEntity>();

    public DbSet<LegacyAuditedEntity> AuditedEntities => Set<LegacyAuditedEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LegacyProduct>(entity =>
        {
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).IsRequired();
            entity.Property(product => product.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            entity.HasMany(product => product.Tags)
                .WithOne(tag => tag.Product)
                .HasForeignKey(tag => tag.ProductId);
        });

        modelBuilder.Entity<LegacyProductTag>(entity =>
        {
            entity.HasKey(tag => tag.Id);
            entity.Property(tag => tag.Label).IsRequired();
            entity.HasMany(tag => tag.Details)
                .WithOne(detail => detail.Tag)
                .HasForeignKey(detail => detail.TagId);
        });

        modelBuilder.Entity<LegacyProductTagDetail>(entity =>
        {
            entity.HasKey(detail => detail.Id);
            entity.Property(detail => detail.Note).IsRequired();
        });

        modelBuilder.Entity<LegacySoftEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<LegacyStringEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<LegacyAuditedEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.ApplyMecesoftSoftDeleteFilters();
    }
}

public sealed class LegacyUnfilteredDbContext : DbContext
{
    public LegacyUnfilteredDbContext(DbContextOptions<LegacyUnfilteredDbContext> options)
        : base(options)
    {
    }

    public DbSet<LegacySoftEntity> SoftEntities => Set<LegacySoftEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LegacySoftEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class LegacyIdentityDbContext : IdentityDbContext<IdentityUser>
{
    public LegacyIdentityDbContext(DbContextOptions<LegacyIdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<LegacyProduct> Products => Set<LegacyProduct>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<LegacyProduct>(entity =>
        {
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name).IsRequired();
        });
    }
}
