using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class OwnedWriteAuthorizationTests
{
    [Fact]
    public async Task TrySetReadWithOwnedDeletedFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        context.Entry(fabricated.Detail!).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task TrySetReadWithOwnedModifiedFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        fabricated.Detail!.Name = "changed";

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task TrySetReadWithOwnedAddedFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId, withDetail: false);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.Attach(seeded);
        seeded.Detail = new OwnedDetail { Name = "new-detail" };

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task ResolverReadGrantDoesNotAuthorizeOwnedWrite()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        var coordinator = new TenantScopeCoordinator<OwnedOwner, Guid>(
            new FixedOwnedOwnerResolver(tenantId),
            store);

        DataResult<int> readResult = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            _ => ValueTask.FromResult(0));
        Assert.True(readResult.IsSuccess);

        context.Attach(seeded);
        seeded.Detail!.Name = "changed";

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task OwnerUnchangedWithOwnedDeletedFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        context.Entry(fabricated.Detail!).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task FabricatedOwnerAttachWithOwnedDeletedFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
        context.AuthorizeReadForTesting(tenantId);
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        context.Entry(fabricated.Detail!).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task CrossTenantOwnerWithOwnedDeleteFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid ownerTenant = Guid.NewGuid();
        Guid scopedTenant = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, ownerTenant);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(scopedTenant, TenantOperation.HardDelete));
        store.AuthorizeWriteForTesting(
            scopedTenant,
            TenantOperation.HardDelete);
        context.AuthorizeReadForTesting(scopedTenant);
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        context.Entry(fabricated.Detail!).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
    }

    [Fact]
    public async Task WrongOperationGrantForOwnedDeleteFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        context.AuthorizeReadForTesting(tenantId);
        OwnedOwner fabricated = AttachFabricatedOwner(context, seeded);
        context.Entry(fabricated.Detail!).State = EntityState.Deleted;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task SyncAndAsyncSaveChangesBothRejectOwnedWriteWithoutGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var asyncStore = new TenantScopeStore();
        await using (OwnedSecurityDbContext asyncContext =
            CreateContext(database, asyncStore))
        {
            Assert.True(asyncStore.TrySet(tenantId, TenantOperation.Update));
            asyncContext.AuthorizeReadForTesting(tenantId);
            OwnedOwner fabricated = AttachFabricatedOwner(
                asyncContext,
                seeded);
            fabricated.Detail!.Name = "changed";
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => asyncContext.SaveChangesAsync());
        }

        var syncStore = new TenantScopeStore();
        await using (OwnedSecurityDbContext syncContext =
            CreateContext(database, syncStore))
        {
            Assert.True(syncStore.TrySet(tenantId, TenantOperation.Update));
            syncContext.AuthorizeReadForTesting(tenantId);
            OwnedOwner fabricated = AttachFabricatedOwner(
                syncContext,
                seeded);
            fabricated.Detail!.Name = "changed";
            Assert.Throws<SaveChangesSecurityException>(
                () => syncContext.SaveChanges());
        }
    }

    [Fact]
    public async Task SyncAndAsyncOwnedDeleteWithoutGrantBothFailClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var asyncStore = new TenantScopeStore();
        await using (OwnedSecurityDbContext asyncContext =
            CreateContext(database, asyncStore))
        {
            Assert.True(asyncStore.TrySet(tenantId, TenantOperation.HardDelete));
            asyncContext.AuthorizeReadForTesting(tenantId);
            OwnedOwner fabricated = AttachFabricatedOwner(
                asyncContext,
                seeded);
            asyncContext.Entry(fabricated.Detail!).State = EntityState.Deleted;
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => asyncContext.SaveChangesAsync());
        }

        var syncStore = new TenantScopeStore();
        await using (OwnedSecurityDbContext syncContext =
            CreateContext(database, syncStore))
        {
            Assert.True(syncStore.TrySet(tenantId, TenantOperation.HardDelete));
            syncContext.AuthorizeReadForTesting(tenantId);
            OwnedOwner fabricated = AttachFabricatedOwner(
                syncContext,
                seeded);
            syncContext.Entry(fabricated.Detail!).State = EntityState.Deleted;
            Assert.Throws<SaveChangesSecurityException>(
                () => syncContext.SaveChanges());
        }
    }

    [Fact]
    public async Task OwnedEntryWithoutTrackedOwnerFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
        store.AuthorizeWriteForTesting(
            tenantId,
            TenantOperation.HardDelete);
        context.AuthorizeReadForTesting(tenantId);

        context.Attach(seeded);
        context.Entry(seeded).State = EntityState.Detached;
        context.Entry(seeded.Detail!).State = EntityState.Deleted;

        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task MixedOwnerUpdateAndOwnedDeleteFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        context.Attach(seeded);
        seeded.Name = "changed-owner";
        context.Entry(seeded).State = EntityState.Modified;
        context.Entry(seeded.Detail!).State = EntityState.Deleted;

        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task MixedOwnerUpdateAndOwnedCreateFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId, withDetail: false);

        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Update));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        context.Attach(seeded);
        seeded.Name = "changed-owner";
        context.Entry(seeded).State = EntityState.Modified;
        seeded.Detail = new OwnedDetail { Name = "new-detail" };

        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AuthorizedOwnedCreateSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId, withDetail: false);

        var store = new TenantScopeStore();
        await using (OwnedSecurityDbContext context = CreateContext(database, store))
        {
            Assert.True(store.TrySet(tenantId, TenantOperation.Create));
            store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
            context.Attach(seeded);
            seeded.Detail = new OwnedDetail { Name = "new-detail" };
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            "new-detail",
            await ReadDetailNameAsync(database, tenantId, seeded.Id));
    }

    [Fact]
    public async Task AuthorizedOwnedUpdateSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using (OwnedSecurityDbContext context = CreateContext(database, store))
        {
            Assert.True(store.TrySet(tenantId, TenantOperation.Update));
            store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
            context.Attach(seeded);
            seeded.Detail!.Name = "updated-detail";
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            "updated-detail",
            await ReadDetailNameAsync(database, tenantId, seeded.Id));
    }

    [Fact]
    public async Task AuthorizedOwnedDeleteSucceeds()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantId = Guid.NewGuid();
        OwnedOwner seeded = await SeedOwnerAsync(database, tenantId);

        var store = new TenantScopeStore();
        await using (OwnedSecurityDbContext context = CreateContext(database, store))
        {
            Assert.True(store.TrySet(tenantId, TenantOperation.HardDelete));
            store.AuthorizeWriteForTesting(
                tenantId,
                TenantOperation.HardDelete);
            context.Attach(seeded);
            context.Entry(seeded.Detail!).State = EntityState.Deleted;
            await context.SaveChangesAsync();
        }

        Assert.Null(await ReadDetailNameAsync(database, tenantId, seeded.Id));
    }

    private static OwnedSecurityDbContext CreateContext(
        SqliteTestDatabase database,
        TenantScopeStore store)
    {
        return new OwnedSecurityDbContext(
            database.Options<OwnedSecurityDbContext>(),
            store,
            [
                new TenantEntityMapping<OwnedOwner, Guid>(
                    owner => owner.TenantId),
                new TenantEntityMapping<OwnedPrincipal, Guid>(
                    principal => principal.TenantId),
            ],
            []);
    }

    private static async Task<OwnedOwner> SeedOwnerAsync(
        SqliteTestDatabase database,
        Guid tenantId,
        bool withDetail = true)
    {
        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        await context.Database.EnsureCreatedAsync();

        var owner = new OwnedOwner
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "owner",
            Detail = withDetail
                ? new OwnedDetail { Name = "detail" }
                : null,
        };

        Assert.True(store.TrySet(tenantId, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Owners.Add(owner);
        await context.SaveChangesAsync();
        return owner;
    }

    private static OwnedOwner AttachFabricatedOwner(
        OwnedSecurityDbContext context,
        OwnedOwner seeded)
    {
        var fabricated = new OwnedOwner
        {
            Id = seeded.Id,
            TenantId = seeded.TenantId,
            Name = seeded.Name,
            Detail = new OwnedDetail
            {
                Name = seeded.Detail?.Name ?? "detail",
            },
        };
        context.Attach(fabricated);
        return fabricated;
    }

    private static async Task<string?> ReadDetailNameAsync(
        SqliteTestDatabase database,
        Guid tenantId,
        Guid ownerId)
    {
        var store = new TenantScopeStore();
        await using OwnedSecurityDbContext context = CreateContext(database, store);
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        OwnedOwner? owner = await context.Owners
            .AsNoTracking()
            .Include(item => item.Detail)
            .SingleOrDefaultAsync(item => item.Id == ownerId);
        return owner?.Detail?.Name;
    }

    private sealed class FixedOwnedOwnerResolver(Guid tenantId)
        : ITenantAccessResolver<OwnedOwner, Guid>
    {
        public Expression<Func<OwnedOwner, Guid>> TenantIdSelector
            => owner => owner.TenantId;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    new TenantScope<Guid>(tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            OwnedOwner root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(root.TenantId == tenantId));
        }
    }
}
