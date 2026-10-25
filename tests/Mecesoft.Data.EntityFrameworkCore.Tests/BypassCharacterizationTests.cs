using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Characterization tests for operations that intentionally bypass the central SaveChanges
/// validation. These tests document limits; they do not claim that the adapter blocks them.
/// </summary>
public sealed class BypassCharacterizationTests
{
    [Fact]
    public async Task IgnoreQueryFiltersCanReadAnotherTenantsRows()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, NewOrder(tenantA, "A"));
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, NewOrder(tenantB, "B"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Read));

        List<Order> allRows = await context.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .ToListAsync();

        Assert.Equal(2, allRows.Count);
        Assert.Contains(allRows, order => order.TenantId == tenantB);
    }

    [Fact]
    public async Task NamedTenantFilterRemovalCanReadAnotherTenantsRows()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, NewOrder(tenantA, "A"));
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, NewOrder(tenantB, "B"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Read));

        List<Order> allRows = await context.Orders
            .AsNoTracking()
            .IgnoreQueryFilters([MecesoftQueryFilterKeys.Tenant])
            .ToListAsync();

        Assert.Equal(2, allRows.Count);
        Assert.Contains(allRows, order => order.TenantId == tenantB);
    }

    [Fact]
    public async Task ExecuteUpdateCanChangeTenantAndSoftDeleteFields()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Order order = NewOrder(tenantA, "before");
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, order);
        context.ChangeTracker.Clear();

        int affected = await context.Orders
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == order.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.TenantId, tenantB)
                .SetProperty(candidate => candidate.IsDeleted, true)
                .SetProperty(
                    candidate => candidate.DeletedAtUtc,
                    DateTimeOffset.UtcNow));

        Assert.Equal(1, affected);
        context.ChangeTracker.Clear();
        Order bypassed = Assert.Single(await context.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == order.Id)
            .ToListAsync());
        Assert.Equal(tenantB, bypassed.TenantId);
        Assert.True(bypassed.IsDeleted);
    }

    [Fact]
    public async Task ExecuteDeleteCanPhysicallyDeleteASoftDeletableEntity()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantId = Guid.NewGuid();
        Order order = NewOrder(tenantId, "delete-me");
        await TestSeeds.SaveOrdersAsync(context, store, tenantId, order);
        context.ChangeTracker.Clear();

        int affected = await context.Orders
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == order.Id)
            .ExecuteDeleteAsync();

        Assert.Equal(1, affected);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == order.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task ExecuteSqlRawCanChangePersistedDataWithoutSaveChanges()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantId = Guid.NewGuid();
        Order order = NewOrder(tenantId, "before-raw");
        await TestSeeds.SaveOrdersAsync(context, store, tenantId, order);
        context.ChangeTracker.Clear();

        int affected = await context.Database.ExecuteSqlRawAsync(
            "UPDATE Orders SET Name = {0} WHERE Id = {1}",
            "after-raw",
            order.Id);

        Assert.Equal(1, affected);
        context.ChangeTracker.Clear();
        Order bypassed = Assert.Single(await context.Orders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == order.Id)
            .ToListAsync());
        Assert.Equal("after-raw", bypassed.Name);
    }

    [Fact]
    public async Task IgnoreSoftDeleteStillKeepsTenantFilter()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, NewOrder(tenantA, "A-active"));
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, NewOrder(tenantB, "B-active"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantA);

        List<Order> rows;
        using (SoftDeleteReadAuthorization.Begin(
            tenantA,
            SoftDeleteOperation.SoftDelete))
        {
            rows = await context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Assert.Single(rows);
        Assert.Equal(tenantA, rows[0].TenantId);
    }

    private static Order NewOrder(Guid tenantId, string name)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Version = 1,
        };
    }
}
