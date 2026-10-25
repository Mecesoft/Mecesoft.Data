using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class LegacySecurityBoundaryTests
{
    [Fact]
    public async Task LegacyRepositoryCannotReadTenantRowsWithoutAGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<Order, TestDbContext>(context);

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => repository.GetAll().ToListAsync());
    }

    [Fact]
    public async Task LegacyRepositoryCannotBypassSoftDeleteAuthorization()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        var repository = new Repository<Order, TestDbContext>(context);

        Assert.Empty(await repository.GetAll().ToListAsync());
        Assert.Throws<InvalidOperationException>(
            () => repository.GetAll(includeDeleted: true));
    }

    [Fact]
    public async Task LegacyRepositoryCannotWriteThroughSaveChangesWithoutAGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        var repository = new Repository<Order, TestDbContext>(context);
        await repository.AddAsync(new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "not authorized",
        });

        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => context.SaveChangesAsync());
    }
}
