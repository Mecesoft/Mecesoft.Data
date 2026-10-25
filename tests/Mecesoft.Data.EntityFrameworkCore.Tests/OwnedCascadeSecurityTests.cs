using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class OwnedCascadeSecurityTests
{
    [Fact]
    public async Task OwnedEntityCannotReferenceAnotherTenantsPrincipal()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = CreateOwnedContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        OwnedPrincipal principalB = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantB,
            Name = "B",
        };

        var seedStore = new TenantScopeStore();
        await using (var seed = CreateOwnedContext(database, seedStore))
        {
            seedStore.Clear();
            Assert.True(seedStore.TrySet(tenantB, TenantOperation.Create));
            seedStore.AuthorizeWriteForTesting(
                tenantB,
                TenantOperation.Create);
            seed.Principals.Add(principalB);
            await seed.SaveChangesAsync();
        }

        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantA, TenantOperation.Create);
        context.Principals.Attach(principalB);
        context.Owners.Add(new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            Name = "owner-A",
            Detail = new OwnedDetail
            {
                Name = "detail",
                PrincipalId = principalB.Id,
            },
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task OwnedEntityUpdateCannotReferenceAnotherTenantsPrincipal()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = CreateOwnedContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        var principalA = new OwnedPrincipal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            Name = "A",
        };
        var owner = new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantA,
            Name = "owner-A",
            Detail = new OwnedDetail
            {
                Name = "detail",
                PrincipalId = principalA.Id,
            },
        };
        var principalB = new OwnedPrincipal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantB,
            Name = "B",
        };

        var seedStore = new TenantScopeStore();
        await using (var seed = CreateOwnedContext(database, seedStore))
        {
            seedStore.Clear();
            Assert.True(seedStore.TrySet(tenantB, TenantOperation.Create));
            seedStore.AuthorizeWriteForTesting(
                tenantB,
                TenantOperation.Create);
            seed.Principals.Add(principalB);
            await seed.SaveChangesAsync();
        }

        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantA, TenantOperation.Create);
        context.Principals.Add(principalA);
        context.Owners.Add(owner);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantA);
        OwnedOwner tracked = await context.Owners
            .Include(item => item.Detail)
            .SingleAsync();
        context.ChangeTracker.Clear();
        context.Attach(tracked);
        context.Principals.Attach(principalB);
        tracked.Detail!.PrincipalId = principalB.Id;

        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantA, TenantOperation.Update);
        context.Entry(tracked).State = EntityState.Modified;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task OwnedEntityCanReferenceSameTenantPrincipal()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = CreateOwnedContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        OwnedPrincipal principal = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "same",
        };
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Principals.Add(principal);
        context.Owners.Add(new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "owner",
            Detail = new OwnedDetail
            {
                Name = "detail",
                PrincipalId = principal.Id,
            },
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await context.Owners.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OwnedEntityCanReferenceGlobalPrincipalWithRestrict()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = CreateOwnedContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        OwnedGlobal global = new()
        {
            Id = Guid.NewGuid(),
            Name = "global",
        };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            global,
            TenantOperation.Create);
        context.Globals.Add(global);
        await context.SaveChangesAsync();

        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Owners.Add(new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "owner",
            Detail = new OwnedDetail
            {
                Name = "detail",
                GlobalId = global.Id,
            },
        });

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await context.Owners.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OwnedEntityOwnerLifecycleCascadeStillWorks()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = CreateOwnedContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        var owner = new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "owner",
            Detail = new OwnedDetail { Name = "detail" },
        };
        context.Owners.Add(owner);
        await context.SaveChangesAsync();

        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.HardDelete);
        context.Owners.Remove(owner);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await context.Owners.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void OwnedGlobalCascadeIsRejectedAtModelValidation()
    {
        using var context = new OwnedGlobalCascadeDbContext(
            Options<OwnedGlobalCascadeDbContext>(),
            new TenantScopeStore(),
            OwnedMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
        Assert.Contains(
            "cascade",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwnedTenantPrincipalCascadeIsRejectedAtModelValidation()
    {
        using var context = new OwnedTenantPrincipalCascadeDbContext(
            Options<OwnedTenantPrincipalCascadeDbContext>(),
            new TenantScopeStore(),
            OwnedMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
        Assert.Contains(
            "cascade",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalPrincipalCascadeToTenantDependentIsRejectedAtModelValidation()
    {
        using var context = new GlobalCascadeDbContext(
            Options<GlobalCascadeDbContext>(),
            new TenantScopeStore(),
            GlobalDependentMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
        Assert.Contains(
            "cascade",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalPrincipalClientCascadeToTenantDependentIsRejectedAtModelValidation()
    {
        using var context = new GlobalClientCascadeDbContext(
            Options<GlobalClientCascadeDbContext>(),
            new TenantScopeStore(),
            GlobalDependentMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
        Assert.Contains(
            "cascade",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalPrincipalRestrictAndNoActionAreAllowed()
    {
        using var restrict = new GlobalRestrictDbContext(
            Options<GlobalRestrictDbContext>(),
            new TenantScopeStore(),
            GlobalDependentMappings(),
            []);
        Assert.NotNull(restrict.Model);

        using var noAction = new GlobalNoActionDbContext(
            Options<GlobalNoActionDbContext>(),
            new TenantScopeStore(),
            GlobalDependentMappings(),
            []);
        Assert.NotNull(noAction.Model);
    }

    [Fact]
    public async Task GlobalRestrictDeleteDoesNotRemoveUntrackedTenantDependents()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = new GlobalRestrictDbContext(
            database.Options<GlobalRestrictDbContext>(),
            store,
            GlobalDependentMappings(),
            []);
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        var global = new GlobalLookup
        {
            Id = Guid.NewGuid(),
            Name = "global",
        };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            global,
            TenantOperation.Create);
        context.Globals.Add(global);
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Dependents.Add(new TenantDependent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            GlobalId = global.Id,
            Name = "child",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            global,
            TenantOperation.HardDelete);
        context.Globals.Remove(global);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (Exception)
        {
            // A relational FK constraint may reject the DELETE. The dependent must remain.
        }

        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await context.Dependents.AsNoTracking().ToListAsync());
    }

    private static OwnedSecurityDbContext CreateOwnedContext(
        SqliteTestDatabase database,
        TenantScopeStore store)
    {
        return new OwnedSecurityDbContext(
            database.Options<OwnedSecurityDbContext>(),
            store,
            OwnedMappings(),
            []);
    }

    private static ITenantEntityMapping[] OwnedMappings()
    {
        return
        [
            new TenantEntityMapping<OwnedOwner, Guid>(owner => owner.TenantId),
            new TenantEntityMapping<OwnedPrincipal, Guid>(
                principal => principal.TenantId),
        ];
    }

    private static ITenantEntityMapping[] GlobalDependentMappings()
    {
        return
        [
            new TenantEntityMapping<TenantDependent, Guid>(
                dependent => dependent.TenantId),
        ];
    }

    private static DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
    }
}

public sealed class OwnedPrincipal : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class OwnedOwner : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public OwnedDetail? Detail { get; set; }
}

public sealed class OwnedDetail
{
    public string Name { get; set; } = string.Empty;

    public Guid? PrincipalId { get; set; }

    public OwnedPrincipal? Principal { get; set; }

    public Guid? GlobalId { get; set; }

    public OwnedGlobal? Global { get; set; }
}

public sealed class OwnedGlobal
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class GlobalLookup
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class TenantDependent : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid GlobalId { get; set; }

    public GlobalLookup? Global { get; set; }

    public string Name { get; set; } = string.Empty;
}

public abstract class OwnedSecurityContextBase : MecesoftDbContext
{
    protected OwnedSecurityContextBase(
        DbContextOptions options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<OwnedOwner> Owners => Set<OwnedOwner>();

    public DbSet<OwnedPrincipal> Principals => Set<OwnedPrincipal>();

    public DbSet<OwnedGlobal> Globals => Set<OwnedGlobal>();

    protected abstract DeleteBehavior GlobalDeleteBehavior { get; }

    protected abstract DeleteBehavior PrincipalDeleteBehavior { get; }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnedPrincipal>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });

        modelBuilder.Entity<OwnedOwner>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.OwnsOne(item => item.Detail, detail =>
            {
                detail.Property(item => item.Name).IsRequired();
                detail.HasOne(item => item.Principal)
                    .WithMany()
                    .HasForeignKey(item => item.PrincipalId)
                    .OnDelete(PrincipalDeleteBehavior);
                detail.HasOne(item => item.Global)
                    .WithMany()
                    .HasForeignKey(item => item.GlobalId)
                    .OnDelete(GlobalDeleteBehavior);
            });
        });

        modelBuilder.Entity<OwnedGlobal>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.IsGlobal();
        });
    }
}

public sealed class OwnedSecurityDbContext : OwnedSecurityContextBase
{
    public OwnedSecurityDbContext(
        DbContextOptions<OwnedSecurityDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior GlobalDeleteBehavior => DeleteBehavior.Restrict;

    protected override DeleteBehavior PrincipalDeleteBehavior => DeleteBehavior.Restrict;
}

public sealed class OwnedGlobalCascadeDbContext : OwnedSecurityContextBase
{
    public OwnedGlobalCascadeDbContext(
        DbContextOptions<OwnedGlobalCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior GlobalDeleteBehavior => DeleteBehavior.Cascade;

    protected override DeleteBehavior PrincipalDeleteBehavior => DeleteBehavior.Restrict;
}

public sealed class OwnedTenantPrincipalCascadeDbContext : OwnedSecurityContextBase
{
    public OwnedTenantPrincipalCascadeDbContext(
        DbContextOptions<OwnedTenantPrincipalCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior GlobalDeleteBehavior => DeleteBehavior.Restrict;

    protected override DeleteBehavior PrincipalDeleteBehavior => DeleteBehavior.Cascade;
}

public abstract class GlobalDependentContextBase : MecesoftDbContext
{
    protected GlobalDependentContextBase(
        DbContextOptions options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<GlobalLookup> Globals => Set<GlobalLookup>();

    public DbSet<TenantDependent> Dependents => Set<TenantDependent>();

    protected abstract DeleteBehavior DependentDeleteBehavior { get; }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalLookup>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.IsGlobal();
        });

        modelBuilder.Entity<TenantDependent>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.HasOne(item => item.Global)
                .WithMany()
                .HasForeignKey(item => item.GlobalId)
                .OnDelete(DependentDeleteBehavior);
        });
    }
}

public sealed class GlobalCascadeDbContext : GlobalDependentContextBase
{
    public GlobalCascadeDbContext(
        DbContextOptions<GlobalCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior DependentDeleteBehavior => DeleteBehavior.Cascade;
}

public sealed class GlobalClientCascadeDbContext : GlobalDependentContextBase
{
    public GlobalClientCascadeDbContext(
        DbContextOptions<GlobalClientCascadeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior DependentDeleteBehavior =>
        DeleteBehavior.ClientCascade;
}

public sealed class GlobalRestrictDbContext : GlobalDependentContextBase
{
    public GlobalRestrictDbContext(
        DbContextOptions<GlobalRestrictDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior DependentDeleteBehavior => DeleteBehavior.Restrict;
}

public sealed class GlobalNoActionDbContext : GlobalDependentContextBase
{
    public GlobalNoActionDbContext(
        DbContextOptions<GlobalNoActionDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    protected override DeleteBehavior DependentDeleteBehavior => DeleteBehavior.NoAction;
}
