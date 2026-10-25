using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class TenantScopeStoreTests
{
    [Fact]
    public void StoreRejectsEmptyIdentifiers()
    {
        var store = new TenantScopeStore();

        Assert.False(store.TrySet(Guid.Empty, TenantOperation.Read));
        Assert.False(store.TrySet(" ", TenantOperation.Read));
        Assert.False(store.HasTenant);
    }

    [Fact]
    public void StoreRejectsConflictingTenant()
    {
        var store = new TenantScopeStore();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        Assert.True(store.TrySet(first, TenantOperation.Read));
        Assert.False(store.TrySet(second, TenantOperation.Update));
        Assert.Equal(first, store.TenantId);
    }

    [Fact]
    public void StoreAllowsSameTenantAndUpdatesOperation()
    {
        var store = new TenantScopeStore();
        Guid tenant = Guid.NewGuid();

        Assert.True(store.TrySet(tenant, TenantOperation.Read));
        Assert.True(store.TrySet(tenant, TenantOperation.Update));
        Assert.Equal(TenantOperation.Update, store.Operation);
        Assert.True(store.HasTenant);
    }

    [Fact]
    public void ClearRemovesScope()
    {
        var store = new TenantScopeStore();
        Assert.True(store.TrySet(Guid.NewGuid(), TenantOperation.Read));

        store.Clear();

        Assert.Null(store.TenantId);
        Assert.Null(store.Operation);
        Assert.False(store.HasTenant);
    }
}
