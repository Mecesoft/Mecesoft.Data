using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class EfSoftDeleteRepositoryTests
{
    [Fact]
    public async Task SoftDeleteRequiresAnActiveTransactionAndExecutesNoSql()
    {
        var interceptor = new CapturingCommandInterceptor();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(
            Guid.NewGuid(),
            interceptor);
        Order order = NewOrder(fixture.TenantId, "no-transaction");
        await fixture.SeedAsync(fixture.TenantId, order);
        interceptor.Reset();

        DataResult<Order> softDeleteResult = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));
        DataResult<Order> restoreResult = await fixture.Repository.RestoreAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(softDeleteResult.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(softDeleteResult.Failure);
        Assert.True(restoreResult.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(restoreResult.Failure);
        Assert.Empty(interceptor.Commands);
        Assert.False(fixture.UnitOfWork.HasActiveTransaction);
    }

    [Fact]
    public async Task TenantResolverFailureExecutesNoSqlAndKeepsTransactionActive()
    {
        var interceptor = new CapturingCommandInterceptor();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(
            Guid.NewGuid(),
            interceptor);
        Order order = NewOrder(fixture.TenantId, "resolver-failure");
        await fixture.SeedAsync(fixture.TenantId, order);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        fixture.Resolver.Fail = true;
        interceptor.Reset();

        DataResult<Order> result = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(result.IsFailure);
        Assert.IsType<TenantUnavailableFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);
        Assert.True(fixture.UnitOfWork.HasActiveTransaction);

        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task SoftDeleteHidesRecordAndRestoreMakesItVisible()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "lifecycle");
        await fixture.SeedAsync(tenantId, order);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> deleted = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(deleted.IsSuccess, deleted.Failure?.ToString());
        Assert.True(deleted.Value.IsDeleted);
        Assert.NotNull(deleted.Value.DeletedAtUtc);
        Assert.Equal(2, deleted.Value.Version);
        Assert.True(fixture.UnitOfWork.HasActiveTransaction);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await fixture.Context.Orders.AsNoTracking().ToListAsync());

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> restored = await fixture.Repository.RestoreAsync(
            order.Id,
            new ConcurrencyToken("2"));

        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.False(restored.Value.IsDeleted);
        Assert.Null(restored.Value.DeletedAtUtc);
        Assert.Equal(3, restored.Value.Version);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        List<Order> visible = await fixture.Context.Orders.AsNoTracking().ToListAsync();
        Order persisted = Assert.Single(visible);
        Assert.Equal(order.Id, persisted.Id);
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task SoftDeleteAndRestoreAreTenantIsolated()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using SoftDeleteFixture fixtureA = await SoftDeleteFixture.CreateAsync(database, tenantA);
        await using SoftDeleteFixture fixtureB = await SoftDeleteFixture.CreateAsync(database, tenantB);
        Order orderA = NewOrder(tenantA, "A");
        Order orderB = NewOrder(tenantB, "B");
        await fixtureA.SeedAsync(tenantA, orderA);
        await fixtureB.SeedAsync(tenantB, orderB);

        Assert.True((await fixtureA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixtureA.Repository.SoftDeleteAsync(
            orderA.Id,
            new ConcurrencyToken("1"))).IsSuccess);
        Assert.True((await fixtureA.UnitOfWork.CommitAsync()).IsSuccess);

        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        Assert.Empty(await fixtureA.Context.Orders.AsNoTracking().ToListAsync());

        fixtureB.Store.Clear();
        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Read));
        fixtureB.Context.AuthorizeReadForTesting(tenantB);
        Assert.Single(await fixtureB.Context.Orders.AsNoTracking().ToListAsync());

        Assert.True((await fixtureB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> crossTenantDelete = await fixtureB.Repository.SoftDeleteAsync(
            orderA.Id,
            new ConcurrencyToken("2"));
        Assert.True(crossTenantDelete.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenantDelete.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixtureB.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixtureB.UnitOfWork.CommitAsync()).IsFailure);

        Assert.True((await fixtureB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> crossTenantRestore = await fixtureB.Repository.RestoreAsync(
            orderA.Id,
            new ConcurrencyToken("2"));
        Assert.True(crossTenantRestore.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenantRestore.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixtureB.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixtureB.UnitOfWork.CommitAsync()).IsFailure);

        Assert.True((await fixtureA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> restored = await fixtureA.Repository.RestoreAsync(
            orderA.Id,
            new ConcurrencyToken("2"));
        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.True((await fixtureA.UnitOfWork.CommitAsync()).IsSuccess);

        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        Assert.Single(await fixtureA.Context.Orders.AsNoTracking().ToListAsync());

        Assert.True((await fixtureB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixtureB.Repository.SoftDeleteAsync(
            orderB.Id,
            new ConcurrencyToken("1"))).IsSuccess);
        Assert.True((await fixtureB.UnitOfWork.CommitAsync()).IsSuccess);

        fixtureB.Store.Clear();
        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Read));
        fixtureB.Context.AuthorizeReadForTesting(tenantB);
        Assert.Empty(await fixtureB.Context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnauthorizedSoftDeleteAndRestoreAreRejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order active = NewOrder(tenantId, "active");
        Order deleted = NewOrder(tenantId, "deleted", isDeleted: true);
        await fixture.SeedAsync(tenantId, active, deleted);
        fixture.Authorizer.Allow = false;

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> deniedDelete = await fixture.Repository.SoftDeleteAsync(
            active.Id,
            new ConcurrencyToken("1"));

        Assert.True(deniedDelete.IsFailure);
        AuthorizationFailure deleteFailure = Assert.IsType<AuthorizationFailure>(deniedDelete.Failure);
        Assert.Equal("soft_delete", deleteFailure.Operation);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> deniedRestore = await fixture.Repository.RestoreAsync(
            deleted.Id,
            new ConcurrencyToken("1"));

        Assert.True(deniedRestore.IsFailure);
        AuthorizationFailure restoreFailure = Assert.IsType<AuthorizationFailure>(deniedRestore.Failure);
        Assert.Equal("restore", restoreFailure.Operation);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        List<Order> all;
        using (SoftDeleteReadAuthorization.Begin(
            tenantId,
            SoftDeleteOperation.SoftDelete))
        {
            all = await fixture.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Order stillDeleted = Assert.Single(all, order => order.Id == deleted.Id);
        Assert.True(stillDeleted.IsDeleted);

        Assert.Equal(2, fixture.Authorizer.Contexts.Count);
        Assert.Equal(SoftDeleteOperation.SoftDelete, fixture.Authorizer.Contexts[0].Operation);
        Assert.Equal(active.Id, fixture.Authorizer.Contexts[0].Key);
        Assert.Equal(tenantId, fixture.Authorizer.Contexts[0].TenantId);
        Assert.Equal(SoftDeleteOperation.Restore, fixture.Authorizer.Contexts[1].Operation);
        Assert.Equal(deleted.Id, fixture.Authorizer.Contexts[1].Key);
    }

    [Fact]
    public async Task ConcurrencyConflictRollsBackAndBlocksCommit()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "concurrency");
        await fixture.SeedAsync(tenantId, order);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> result = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("99"));

        Assert.True(result.IsFailure);
        Assert.IsType<ConcurrencyFailure<Guid>>(result.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);
        Assert.Empty(fixture.Context.ChangeTracker.Entries());

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Order persisted = Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task DirectSoftDeleteFieldChangesAreRejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "direct");
        Order deletedOrder = NewOrder(tenantId, "direct-deleted", isDeleted: true);
        await fixture.SeedAsync(tenantId, order, deletedOrder);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        fixture.Store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        Order tracked = await fixture.Context.Orders.SingleAsync();
        tracked.IsDeleted = true;

        SaveChangesSecurityException deletedException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        MutationInvariantFailure deletedFailure =
            Assert.IsType<MutationInvariantFailure>(deletedException.Failure);
        Assert.Equal("soft_delete_state", deletedFailure.Invariant);
        fixture.Context.ChangeTracker.Clear();

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        fixture.Store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);
        tracked = await fixture.Context.Orders.SingleAsync();
        tracked.DeletedAtUtc = DateTimeOffset.UtcNow;

        SaveChangesSecurityException timestampException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        MutationInvariantFailure timestampFailure =
            Assert.IsType<MutationInvariantFailure>(timestampException.Failure);
        Assert.Equal("deleted_at", timestampFailure.Invariant);
        fixture.Context.ChangeTracker.Clear();

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Order trackedDeleted;
        using (SoftDeleteReadAuthorization.Begin(
            tenantId,
            SoftDeleteOperation.SoftDelete))
        {
            trackedDeleted = await fixture.Context.Orders
                .IgnoreSoftDelete()
                .SingleAsync(candidate => candidate.Id == deletedOrder.Id);
        }

        trackedDeleted.IsDeleted = false;
        fixture.Store.AuthorizeWriteForTesting(tenantId, TenantOperation.Update);

        SaveChangesSecurityException restoreException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        MutationInvariantFailure restoreFailure =
            Assert.IsType<MutationInvariantFailure>(restoreException.Failure);
        Assert.Equal("soft_delete_state", restoreFailure.Invariant);
        fixture.Context.ChangeTracker.Clear();

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Order persisted = Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());
        Assert.False(persisted.IsDeleted);
        Assert.Null(persisted.DeletedAtUtc);

        List<Order> all;
        using (SoftDeleteReadAuthorization.Begin(
            tenantId,
            SoftDeleteOperation.SoftDelete))
        {
            all = await fixture.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Order persistedDeleted = Assert.Single(all, candidate => candidate.Id == deletedOrder.Id);
        Assert.True(persistedDeleted.IsDeleted);
    }

    [Fact]
    public async Task DirectPhysicalDeleteOfSoftDeletableEntityIsRejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "physical-delete");
        await fixture.SeedAsync(tenantId, order);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        fixture.Store.AuthorizeWriteForTesting(
            tenantId,
            TenantOperation.HardDelete);
        Order tracked = await fixture.Context.Orders.SingleAsync();
        fixture.Context.Orders.Remove(tracked);

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        MutationInvariantFailure failure =
            Assert.IsType<MutationInvariantFailure>(exception.Failure);
        Assert.Equal("soft_delete_state", failure.Invariant);
        fixture.Context.ChangeTracker.Clear();

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InconsistentCustomVisibilityPredicateFailsClosed()
    {
        Guid tenantId = Guid.NewGuid();
        var mapping = new SoftDeleteEntityMapping<Order>(
            order => order.Name != "hidden");
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateWithSoftDeleteAsync(
            tenantId,
            mapping);
        Order order = NewOrder(tenantId, "visible-name");
        await fixture.SeedAsync(tenantId, order);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> result = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Order persisted = Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task SoftDeleteUsesUpdateAndRestoreKeepsTheTenantPredicate()
    {
        var interceptor = new CapturingCommandInterceptor();
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(
            tenantId,
            interceptor);
        Order active = NewOrder(tenantId, "sql-active");
        Order deleted = NewOrder(tenantId, "sql-deleted", isDeleted: true);
        await fixture.SeedAsync(tenantId, active, deleted);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        interceptor.Reset();
        DataResult<Order> softDeleteResult = await fixture.Repository.SoftDeleteAsync(
            active.Id,
            new ConcurrencyToken("1"));
        Assert.True(softDeleteResult.IsSuccess, softDeleteResult.Failure?.ToString());
        Assert.Contains(
            interceptor.Commands,
            command => command.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            interceptor.Commands,
            command => command.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        interceptor.Reset();
        DataResult<Order> restoreResult = await fixture.Repository.RestoreAsync(
            deleted.Id,
            new ConcurrencyToken("1"));
        Assert.True(restoreResult.IsSuccess, restoreResult.Failure?.ToString());

        Assert.Contains(
            interceptor.Commands,
            command => command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.Contains("TenantId", StringComparison.Ordinal)
                && command.Contains("IsDeleted", StringComparison.Ordinal));
        Assert.DoesNotContain(
            interceptor.Commands,
            command => command.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        List<Order> all;
        using (SoftDeleteReadAuthorization.Begin(
            tenantId,
            SoftDeleteOperation.SoftDelete))
        {
            all = await fixture.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Assert.Equal(2, all.Count);
        Assert.Contains(all, order => order.Id == active.Id && order.IsDeleted);
        Assert.Contains(all, order => order.Id == deleted.Id && !order.IsDeleted);
    }

    [Fact]
    public async Task AuthorizerIsEvaluatedPerResource()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order first = NewOrder(tenantId, "first");
        Order second = NewOrder(tenantId, "second");
        await fixture.SeedAsync(tenantId, first, second);
        fixture.Authorizer.AllowedKey = first.Id;

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixture.Repository.SoftDeleteAsync(
            first.Id,
            new ConcurrencyToken("1"))).IsSuccess);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> denied = await fixture.Repository.SoftDeleteAsync(
            second.Id,
            new ConcurrencyToken("1"));
        Assert.True(denied.IsFailure);
        Assert.IsType<AuthorizationFailure>(denied.Failure);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(2, fixture.Authorizer.Contexts.Count);
        Assert.Equal(first.Id, fixture.Authorizer.Contexts[0].Key);
        Assert.Equal(second.Id, fixture.Authorizer.Contexts[1].Key);
        Assert.Equal(tenantId, fixture.Authorizer.Contexts[0].TenantId);
        Assert.Equal(tenantId, fixture.Authorizer.Contexts[1].TenantId);
    }

    [Fact]
    public async Task AuthorizerCannotReturnADifferentOperation()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "wrong-operation");
        await fixture.SeedAsync(tenantId, order);
        fixture.Authorizer.ReturnedOperation = SoftDeleteOperation.Restore;

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> result = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(result.IsFailure);
        Assert.IsType<AuthorizationFailure>(result.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);
    }

    [Fact]
    public async Task AuthorizerCannotModifyTrackedState()
    {
        Guid tenantId = Guid.NewGuid();
        await using SoftDeleteFixture fixture = await SoftDeleteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "tampered");
        await fixture.SeedAsync(tenantId, order);
        fixture.Authorizer.OnAuthorize = context => context.Entity.Name = "tampered-by-authorizer";

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> result = await fixture.Repository.SoftDeleteAsync(
            order.Id,
            new ConcurrencyToken("1"));

        Assert.True(result.IsFailure);
        Assert.IsType<AuthorizationFailure>(result.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        Order persisted = Assert.Single(await fixture.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("tampered", persisted.Name);
        Assert.False(persisted.IsDeleted);
    }

    [Fact]
    public async Task SharedModelCacheKeepsSoftDeleteTenantIsolation()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using SoftDeleteFixture fixtureA = await SoftDeleteFixture.CreateAsync(database, tenantA);
        await using SoftDeleteFixture fixtureB = await SoftDeleteFixture.CreateAsync(database, tenantB);

        Assert.Same(fixtureA.Context.Model, fixtureB.Context.Model);

        Order orderA = NewOrder(tenantA, "model-A");
        Order orderB = NewOrder(tenantB, "model-B");
        await fixtureA.SeedAsync(tenantA, orderA);
        await fixtureB.SeedAsync(tenantB, orderB);

        Assert.True((await fixtureA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixtureA.Repository.SoftDeleteAsync(
            orderA.Id,
            new ConcurrencyToken("1"))).IsSuccess);
        Assert.True((await fixtureA.UnitOfWork.CommitAsync()).IsSuccess);

        fixtureB.Store.Clear();
        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Read));
        fixtureB.Context.AuthorizeReadForTesting(tenantB);
        List<Order> tenantBOrders = await fixtureB.Context.Orders.AsNoTracking().ToListAsync();
        Order visibleB = Assert.Single(tenantBOrders);
        Assert.Equal(orderB.Id, visibleB.Id);

        Assert.True((await fixtureB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> crossTenantRestore = await fixtureB.Repository.RestoreAsync(
            orderA.Id,
            new ConcurrencyToken("2"));
        Assert.True(crossTenantRestore.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenantRestore.Failure);
        Assert.True((await fixtureB.UnitOfWork.CommitAsync()).IsFailure);
    }

    private static Order NewOrder(
        Guid tenantId,
        string name,
        bool isDeleted = false)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Version = 1,
            IsDeleted = isDeleted,
        };
    }

    private sealed class SoftDeleteFixture : IAsyncDisposable
    {
        private SoftDeleteFixture(
            SqliteTestDatabase database,
            TenantScopeStore store,
            TestDbContext context,
            EfUnitOfWork<TestDbContext> unitOfWork,
            TestSoftDeleteResolver resolver,
            RecordingSoftDeleteAuthorizer authorizer,
            EfSoftDeleteRepository<TestDbContext, Order, Guid, Guid> repository)
        {
            Database = database;
            Store = store;
            Context = context;
            UnitOfWork = unitOfWork;
            Resolver = resolver;
            Authorizer = authorizer;
            Repository = repository;
        }

        public SqliteTestDatabase Database { get; }

        public TenantScopeStore Store { get; }

        public TestDbContext Context { get; }

        public EfUnitOfWork<TestDbContext> UnitOfWork { get; }

        public TestSoftDeleteResolver Resolver { get; }

        public RecordingSoftDeleteAuthorizer Authorizer { get; }

        public EfSoftDeleteRepository<TestDbContext, Order, Guid, Guid> Repository { get; }

        public Guid TenantId => Resolver.TenantId;

        public static async Task<SoftDeleteFixture> CreateAsync(
            Guid tenantId,
            CapturingCommandInterceptor? interceptor = null)
        {
            SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
            return await CreateAsync(database, tenantId, interceptor);
        }

        public static async Task<SoftDeleteFixture> CreateWithSoftDeleteAsync(
            Guid tenantId,
            ISoftDeleteEntityMapping softDeleteMapping)
        {
            SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
            var store = new TenantScopeStore();
            TestDbContext context = TestContexts.CreateWithSoftDelete(
                database,
                store,
                softDeleteMapping,
                TestContexts.OrderTenantMapping());
            await context.Database.EnsureCreatedAsync();
            var resolver = new TestSoftDeleteResolver(tenantId);
            var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
            var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
            var authorizer = new RecordingSoftDeleteAuthorizer();
            var repository = new EfSoftDeleteRepository<TestDbContext, Order, Guid, Guid>(
                context,
                coordinator,
                unitOfWork,
                authorizer,
                order => order.Id);

            return new SoftDeleteFixture(
                database,
                store,
                context,
                unitOfWork,
                resolver,
                authorizer,
                repository);
        }

        public static async Task<SoftDeleteFixture> CreateAsync(
            SqliteTestDatabase database,
            Guid tenantId,
            CapturingCommandInterceptor? interceptor = null)
        {
            var store = new TenantScopeStore();
            TestDbContext context = interceptor is null
                ? TestContexts.Create(database, store, TestContexts.OrderTenantMapping())
                : TestContexts.CreateWithInterceptor(
                    database,
                    store,
                    interceptor,
                    TestContexts.OrderTenantMapping());
            await context.Database.EnsureCreatedAsync();
            var resolver = new TestSoftDeleteResolver(tenantId);
            var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
            var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
            var authorizer = new RecordingSoftDeleteAuthorizer();
            var repository = new EfSoftDeleteRepository<TestDbContext, Order, Guid, Guid>(
                context,
                coordinator,
                unitOfWork,
                authorizer,
                order => order.Id);

            return new SoftDeleteFixture(
                database,
                store,
                context,
                unitOfWork,
                resolver,
                authorizer,
                repository);
        }

        public async Task SeedAsync(Guid tenantId, params Order[] orders)
        {
            await TestSeeds.SaveOrdersAsync(Context, Store, tenantId, orders);
            Context.ChangeTracker.Clear();
            Store.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await UnitOfWork.DisposeAsync();
            await Context.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class TestSoftDeleteResolver(Guid tenantId)
        : ITenantAccessResolver<Order, Guid>
    {
        public Guid TenantId { get; } = tenantId;

        public bool Fail { get; set; }

        public Expression<Func<Order, Guid>> TenantIdSelector => order => order.TenantId;

        public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                return ValueTask.FromResult(
                    DataResult.Failed<TenantScope<Guid>>(
                        new TenantUnavailableFailure("Tenant is unavailable.")));
            }

            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(TenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            Order root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == TenantId));
        }
    }

    private sealed class RecordingSoftDeleteAuthorizer
        : ISoftDeleteAuthorizer<Order, Guid, Guid>
    {
        public bool Allow { get; set; } = true;

        public Guid? AllowedKey { get; set; }

        public SoftDeleteOperation? ReturnedOperation { get; set; }

        public Action<SoftDeleteAuthorizationContext<Order, Guid, Guid>>? OnAuthorize { get; set; }

        public List<SoftDeleteAuthorizationContext<Order, Guid, Guid>> Contexts { get; } = [];

        public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
            SoftDeleteAuthorizationContext<Order, Guid, Guid> context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            OnAuthorize?.Invoke(context);

            bool allowed = Allow
                && (AllowedKey is null || AllowedKey == context.Key);

            if (!allowed)
            {
                return ValueTask.FromResult(
                    DataResult.Failed<SoftDeleteOperation>(
                        new AuthorizationFailure(
                            context.Operation == SoftDeleteOperation.SoftDelete
                                ? "soft_delete"
                                : "restore",
                            "Denied by the test authorizer.")));
            }

            return ValueTask.FromResult(
                DataResult.Succeeded(ReturnedOperation ?? context.Operation));
        }
    }
}
