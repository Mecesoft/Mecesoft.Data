using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Authorization;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class GlobalWriteAuthorizationTests
{
    [Fact]
    public async Task GlobalAddWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();

        var country = new GlobalCountry { Id = Guid.NewGuid(), Name = "global" };
        context.Countries.Add(country);

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task GlobalUpdateWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        GlobalCountry country = await SeedCountryAsync(context, "before");
        context.ChangeTracker.Clear();

        GlobalCountry tracked = await context.Countries.SingleAsync();
        tracked.Name = "after";
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task GlobalDeleteWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        _ = await SeedCountryAsync(context, "delete");
        context.ChangeTracker.Clear();

        GlobalCountry tracked = await context.Countries.SingleAsync();
        context.Countries.Remove(tracked);
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task TrySetOnlyDoesNotAuthorizeGlobalWrite()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using GlobalLookupDbContext context = CreateLookupContext(database, store);
        await context.Database.EnsureCreatedAsync();

        Assert.True(store.TrySet(Guid.NewGuid(), TenantOperation.Create));
        context.Countries.Add(new GlobalCountry
        {
            Id = Guid.NewGuid(),
            Name = "tryset",
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task AuthorizedGlobalAddUpdateDeleteSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();

        var country = new GlobalCountry { Id = Guid.NewGuid(), Name = "created" };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            country,
            TenantOperation.Create);
        context.Countries.Add(country);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        GlobalCountry tracked = await context.Countries.SingleAsync();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            tracked,
            TenantOperation.Update);
        tracked.Name = "updated";
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        GlobalCountry deleted = await context.Countries.SingleAsync();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            deleted,
            TenantOperation.HardDelete);
        context.Countries.Remove(deleted);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        Assert.Empty(await context.Countries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GlobalWriteGrantCannotBeReusedByAnotherContext()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        DbContextOptions<GlobalLookupDbContext> options =
            database.Options<GlobalLookupDbContext>();
        await using var contextA = new GlobalLookupDbContext(
            options,
            store,
            [new TenantEntityMapping<GlobalOrder, Guid>(order => order.TenantId)],
            []);
        await using var contextB = new GlobalLookupDbContext(
            options,
            store,
            [new TenantEntityMapping<GlobalOrder, Guid>(order => order.TenantId)],
            []);
        await contextA.Database.EnsureCreatedAsync();

        var countryA = new GlobalCountry { Id = Guid.NewGuid(), Name = "A" };
        await GlobalWriteTestSupport.AuthorizeAsync(
            contextA,
            countryA,
            TenantOperation.Create);
        contextB.Countries.Add(new GlobalCountry
        {
            Id = Guid.NewGuid(),
            Name = "B",
        });

        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => contextB.SaveChangesAsync());

        contextA.Countries.Add(countryA);
        await contextA.SaveChangesAsync();
    }

    [Fact]
    public async Task GlobalRootOwnedAddWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalRootDbContext context = CreateRootContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();

        context.Roots.Add(new GlobalRoot
        {
            Id = Guid.NewGuid(),
            Name = "root",
            Detail = new GlobalRootDetail { Value = "detail" },
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task TrySetOnlyDoesNotAuthorizeGlobalRootOwnedWrite()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using GlobalRootDbContext context = CreateRootContext(database, store);
        await context.Database.EnsureCreatedAsync();
        Assert.True(store.TrySet(Guid.NewGuid(), TenantOperation.Create));

        context.Roots.Add(new GlobalRoot
        {
            Id = Guid.NewGuid(),
            Name = "root",
            Detail = new GlobalRootDetail { Value = "detail" },
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task AuthorizedGlobalRootOwnedAddUpdateDeleteSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalRootDbContext context = CreateRootContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();

        var root = new GlobalRoot
        {
            Id = Guid.NewGuid(),
            Name = "root",
            Detail = new GlobalRootDetail { Value = "created" },
        };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            root,
            TenantOperation.Create);
        context.Roots.Add(root);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        GlobalRoot tracked = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            tracked,
            TenantOperation.Update);
        tracked.Detail!.Value = "updated";
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        GlobalRoot deleted = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            deleted,
            TenantOperation.HardDelete);
        context.Roots.Remove(deleted);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        Assert.Empty(await context.Roots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GlobalRootOwnedUpdateWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalRootDbContext context = CreateRootContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        await SeedRootAsync(context);
        context.ChangeTracker.Clear();

        GlobalRoot tracked = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        tracked.Detail!.Value = "unauthorized";
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);

        tracked.Detail.Value = "authorized";
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            tracked,
            TenantOperation.Update);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task AuthorizedGlobalRootOwnedDirectDeleteSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalRootDbContext context = CreateRootContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        _ = await SeedRootAsync(context);
        context.ChangeTracker.Clear();

        GlobalRoot tracked = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            tracked,
            TenantOperation.HardDelete);
        context.Entry(tracked.Detail!).State = EntityState.Deleted;
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        GlobalRoot persisted = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        Assert.Null(persisted.Detail);
    }

    [Fact]
    public async Task GlobalRootOwnedDeleteWithoutAuthorizationFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalRootDbContext context = CreateRootContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        _ = await SeedRootAsync(context);
        context.ChangeTracker.Clear();

        GlobalRoot tracked = await context.Roots
            .Include(item => item.Detail)
            .SingleAsync();
        context.Entry(tracked.Detail!).State = EntityState.Deleted;
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task MixedGlobalAndTenantGraphRequiresBothAuthorizations()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using GlobalLookupDbContext context = CreateLookupContext(database, store);
        await context.Database.EnsureCreatedAsync();
        Guid tenantId = Guid.NewGuid();

        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        var country = new GlobalCountry { Id = Guid.NewGuid(), Name = "global" };
        var order = new GlobalOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CountryId = country.Id,
        };
        context.Countries.Add(country);
        context.Orders.Add(order);

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);

        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        var country2 = new GlobalCountry { Id = Guid.NewGuid(), Name = "global-2" };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            country2,
            TenantOperation.Create);
        context.Countries.Add(country2);
        context.Orders.Add(new GlobalOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CountryId = country2.Id,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GlobalWriteGrantIsConsumedBySaveChanges()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();

        var first = new GlobalCountry { Id = Guid.NewGuid(), Name = "first" };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            first,
            TenantOperation.Create);
        context.Countries.Add(first);
        await context.SaveChangesAsync();

        context.Countries.Add(new GlobalCountry
        {
            Id = Guid.NewGuid(),
            Name = "second",
        });
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task GlobalReadDoesNotRequireWriteAuthorization()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using GlobalLookupDbContext context = CreateLookupContext(
            database,
            new TenantScopeStore());
        await context.Database.EnsureCreatedAsync();
        _ = await SeedCountryAsync(context, "readable");
        context.ChangeTracker.Clear();

        GlobalCountry country = await context.Countries.SingleAsync();
        Assert.Equal("readable", country.Name);
    }

    private static GlobalLookupDbContext CreateLookupContext(
        SqliteTestDatabase database,
        TenantScopeStore store)
    {
        return new GlobalLookupDbContext(
            database.Options<GlobalLookupDbContext>(),
            store,
            [new TenantEntityMapping<GlobalOrder, Guid>(order => order.TenantId)],
            []);
    }

    private static GlobalRootDbContext CreateRootContext(
        SqliteTestDatabase database,
        TenantScopeStore store)
    {
        return new GlobalRootDbContext(
            database.Options<GlobalRootDbContext>(),
            store,
            [],
            []);
    }

    private static async Task<GlobalCountry> SeedCountryAsync(
        GlobalLookupDbContext context,
        string name)
    {
        var country = new GlobalCountry { Id = Guid.NewGuid(), Name = name };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            country,
            TenantOperation.Create);
        context.Countries.Add(country);
        await context.SaveChangesAsync();
        return country;
    }

    private static async Task<GlobalRoot> SeedRootAsync(
        GlobalRootDbContext context)
    {
        var root = new GlobalRoot
        {
            Id = Guid.NewGuid(),
            Name = "root",
            Detail = new GlobalRootDetail { Value = "detail" },
        };
        await GlobalWriteTestSupport.AuthorizeAsync(
            context,
            root,
            TenantOperation.Create);
        context.Roots.Add(root);
        await context.SaveChangesAsync();
        return root;
    }
}

public sealed class GlobalRoot
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public GlobalRootDetail? Detail { get; set; }
}

public sealed class GlobalRootDetail
{
    public string Value { get; set; } = string.Empty;
}

public sealed class GlobalRootDbContext : MecesoftDbContext
{
    public GlobalRootDbContext(
        DbContextOptions<GlobalRootDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<GlobalRoot> Roots => Set<GlobalRoot>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GlobalRoot>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.OwnsOne(item => item.Detail, detail =>
            {
                detail.Property(item => item.Value).IsRequired();
            });
            entity.IsGlobal();
        });
    }
}
