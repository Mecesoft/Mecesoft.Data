using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class DatabaseOwnershipVerificationTests
{
    [Fact]
    public async Task MissingDatabaseRowOnModifiedAsyncFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        var missing = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "missing",
            Version = 1,
        };
        context.Attach(missing);
        context.Entry(missing).State = EntityState.Modified;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task MissingDatabaseRowOnModifiedSyncFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        var missing = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "missing",
            Version = 1,
        };
        context.Attach(missing);
        context.Entry(missing).State = EntityState.Modified;

        SaveChangesSecurityException exception =
            Assert.Throws<SaveChangesSecurityException>(
                () => context.SaveChanges());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task MissingDatabaseRowOnDeletedAsyncFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
        store.AuthorizeWriteForTesting(
            tenantId,
            TenantOperation.HardDelete);
        var missing = new OrderLine
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrderId = Guid.NewGuid(),
        };
        context.Attach(missing);
        context.Entry(missing).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task MissingDatabaseRowOnDeletedSyncFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
        store.AuthorizeWriteForTesting(
            tenantId,
            TenantOperation.HardDelete);
        var missing = new OrderLine
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrderId = Guid.NewGuid(),
        };
        context.Attach(missing);
        context.Entry(missing).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            Assert.Throws<SaveChangesSecurityException>(
                () => context.SaveChanges());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }
}
