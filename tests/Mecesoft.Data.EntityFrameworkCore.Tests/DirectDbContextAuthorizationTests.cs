using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class DirectDbContextAuthorizationTests
{
    [Fact]
    public async Task ReadScopeDirectTenantWriteFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "existing"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);

        context.Orders.Add(NewOrder(tenantId, "read-scope-write"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<AuthorizationFailure>(exception.Failure);
        context.ChangeTracker.Clear();

        Assert.Single(await context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TenantScopeStoreTrySetAloneDoesNotAuthorizeWrite()
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
        context.AuthorizeReadForTesting(tenantId);
        context.Orders.Add(NewOrder(tenantId, "tryset-only"));

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<AuthorizationFailure>(exception.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DirectDeletedReadRequiresControlledSoftDeleteScope()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        Order deleted = NewOrder(tenantId, "deleted");
        deleted.IsDeleted = true;
        deleted.DeletedAtUtc = DateTimeOffset.UtcNow;
        await TestSeeds.SaveOrdersAsync(context, store, tenantId, deleted);
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.Orders.IgnoreSoftDelete());

        Assert.Contains(
            "authorized soft-delete",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectSoftDeleteStateChangeIsRejectedAfterWriteAuthorization()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "direct-state"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);

        Order tracked = await context.Orders.SingleAsync();
        tracked.IsDeleted = true;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        MutationInvariantFailure failure =
            Assert.IsType<MutationInvariantFailure>(exception.Failure);
        Assert.Equal("soft_delete_state", failure.Invariant);
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task TenantScopeStoreTrySetClearsExistingWriteAuthorization()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        context.AuthorizeReadForTesting(tenantId);
        context.Orders.Add(NewOrder(tenantId, "tryset-after-grant"));

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<AuthorizationFailure>(exception.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Orders.AsNoTracking().ToListAsync());
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
