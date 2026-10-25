using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class EfWriteRepositoryTests
{
    [Fact]
    public async Task AddRequiresActiveTransactionAndMatchingTenant()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());

        DataResult<Order> noTransaction = await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "no-transaction"));
        Assert.True(noTransaction.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(noTransaction.Failure);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> added = await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "added"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True(fixture.UnitOfWork.HasActiveTransaction);

        DataResult<Order> mismatch = await fixture.Repository.AddAsync(
            NewOrder(Guid.NewGuid(), "wrong-tenant"));
        Assert.True(mismatch.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(mismatch.Failure);

        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);
        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        List<Order> orders = await verification.Orders.AsNoTracking().ToListAsync();
        Assert.Single(orders);
        Assert.Equal("added", orders[0].Name);
    }

    [Fact]
    public async Task AddCommitKeepsTenantIsolation()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixtureA = await WriteFixture.CreateAsync(database, tenantA);
        await using WriteFixture fixtureB = await WriteFixture.CreateAsync(database, tenantB);

        Assert.True((await fixtureA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixtureA.Repository.AddAsync(NewOrder(tenantA, "A"))).IsSuccess);
        Assert.True((await fixtureA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await fixtureB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixtureB.Repository.AddAsync(NewOrder(tenantB, "B"))).IsSuccess);
        Assert.True((await fixtureB.UnitOfWork.CommitAsync()).IsSuccess);
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        fixtureB.Context.AuthorizeReadForTesting(tenantB);

        Assert.Single(await fixtureA.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Single(await fixtureB.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(
            tenantA,
            (await fixtureA.Context.Orders.AsNoTracking().SingleAsync()).TenantId);
        Assert.Equal(
            tenantB,
            (await fixtureB.Context.Orders.AsNoTracking().SingleAsync()).TenantId);
    }

    [Fact]
    public async Task UpdateRequiresTransactionAndRejectsCrossTenantKey()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Guid otherTenant = Guid.NewGuid();
        Order ownOrder = NewOrder(fixture.TenantId, "own");
        Order otherOrder = NewOrder(otherTenant, "other");
        await fixture.SeedAsync(fixture.TenantId, ownOrder);
        await fixture.SeedAsync(otherTenant, otherOrder);

        DataResult<EntityUpdateResult<Order>> noTransaction = await fixture.Repository.UpdateAsync(
            ownOrder.Id,
            new ConcurrencyToken("1"),
            NoOpMutation);
        Assert.True(noTransaction.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(noTransaction.Failure);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> crossTenant = await fixture.Repository.UpdateAsync(
            otherOrder.Id,
            new ConcurrencyToken("1"),
            NoOpMutation);
        Assert.True(crossTenant.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenant.Failure);
        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task UpdateProtectsTenantKeySoftDeleteAndConcurrencyInvariants()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "protected");
        await fixture.SeedAsync(fixture.TenantId, entity);

        (Action<Order> Mutation, string Invariant)[] cases =
        [
            (order => order.TenantId = Guid.NewGuid(), "tenant_id"),
            (order => order.Id = Guid.NewGuid(), "primary_key"),
            (order => order.IsDeleted = true, "soft_delete_state"),
            (order => order.DeletedAtUtc = DateTimeOffset.UtcNow, "deleted_at"),
            (order => order.Version = 99, "concurrency_token"),
        ];

        foreach ((Action<Order> mutation, string invariant) in cases)
        {
            Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
            DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
                entity.Id,
                new ConcurrencyToken("1"),
                (order, _) =>
                {
                    mutation(order);
                    return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
                });

            Assert.True(result.IsFailure);
            MutationInvariantFailure failure = Assert.IsType<MutationInvariantFailure>(result.Failure);
            Assert.Equal(invariant, failure.Invariant);
            Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);
            Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);
        }

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(fixture.TenantId, persisted.TenantId);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task UpdateReturnsNewConcurrencyTokenAndRejectsStaleToken()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "before");
        await fixture.SeedAsync(fixture.TenantId, entity);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> updated = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "after";
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });

        Assert.True(updated.IsSuccess, updated.Failure?.ToString());
        Assert.Equal("2", updated.Value.ConcurrencyToken.Value);
        Assert.Equal("after", updated.Value.Entity.Name);
        Assert.True(fixture.UnitOfWork.HasActiveTransaction);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsSuccess);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("after", persisted.Name);
        Assert.Equal(2, persisted.Version);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> stale = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            NoOpMutation);
        Assert.True(stale.IsFailure);
        ConcurrencyFailure<Guid> concurrency = Assert.IsType<ConcurrencyFailure<Guid>>(stale.Failure);
        Assert.Equal("1", concurrency.ExpectedToken.Value);
        Assert.Equal("2", concurrency.CurrentToken!.Value);
        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task DatabaseConcurrencyConflictReturnsConcurrencyFailureAndRollsBack()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "concurrent");
        await fixture.SeedAsync(fixture.TenantId, entity);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                fixture.Context.Database.ExecuteSqlRaw(
                    "UPDATE Orders SET Version = 99 WHERE Id = {0}",
                    order.Id);
                order.Name = "concurrent-after";
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });

        Assert.True(result.IsFailure);
        Assert.IsType<ConcurrencyFailure<Guid>>(result.Failure);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("concurrent", persisted.Name);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task MutationFailureDoesNotPersistAndBlocksCommit()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "before");
        await fixture.SeedAsync(fixture.TenantId, entity);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "mutated";
                return ValueTask.FromResult(
                    DataResult.Failed<EntityMutationResult>(
                        new ValidationFailure("The mutation is invalid.")));
            });

        Assert.True(result.IsFailure);
        Assert.IsType<ValidationFailure>(result.Failure);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await fixture.UnitOfWork.CommitAsync()).IsFailure);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("before", persisted.Name);
    }

    [Fact]
    public async Task GraphValidationFailureRejectsAddAndUpdate()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(
            Guid.NewGuid(),
            graphInScope: false);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> add = await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "graph-add"));
        Assert.True(add.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(add.Failure);
        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);

        Order entity = NewOrder(fixture.TenantId, "graph-update");
        await fixture.SeedAsync(fixture.TenantId, entity);
        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> update = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "changed";
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });

        Assert.True(update.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(update.Failure);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task AddRejectsSoftDeletedEntity()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "soft-deleted");
        entity.IsDeleted = true;
        entity.DeletedAtUtc = DateTimeOffset.UtcNow;

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<Order> result = await fixture.Repository.AddAsync(entity);

        Assert.True(result.IsFailure);
        MutationInvariantFailure failure = Assert.IsType<MutationInvariantFailure>(result.Failure);
        Assert.Equal("soft_delete_state", failure.Invariant);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnloadedNavigationBypassIsClosedBySaveChanges()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        fixture.Resolver.CheckLines = false;
        Order entity = NewOrder(fixture.TenantId, "unloaded-before");
        await fixture.SeedAsync(fixture.TenantId, entity);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "unloaded-after";
                order.Lines.Add(new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    TenantId = Guid.NewGuid(),
                });
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });

        Assert.True(result.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(result.Failure);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("unloaded-before", persisted.Name);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PendingTrackedChangesAreNotDiscarded()
    {
        var interceptor = new CapturingCommandInterceptor();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(
            Guid.NewGuid(),
            interceptor: interceptor);
        Order entity = NewOrder(fixture.TenantId, "original");
        await fixture.SeedAsync(fixture.TenantId, entity);
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);

        Order tracked = await fixture.Context.Orders.SingleAsync();
        tracked.Name = "pending-change";
        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        interceptor.Reset();

        DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
            tracked.Id,
            new ConcurrencyToken("1"),
            NoOpMutation);

        Assert.True(result.IsFailure);
        Assert.IsType<ValidationFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);
        Assert.True(fixture.UnitOfWork.HasActiveTransaction);
        Assert.Equal("pending-change", tracked.Name);
        EntityEntry<Order> entry = Assert.Single(fixture.Context.ChangeTracker.Entries<Order>());
        Assert.Equal(EntityState.Modified, entry.State);

        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("original", persisted.Name);
    }

    [Fact]
    public async Task CrossTenantNavigationGraphChangeIsRejected()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "graph-before");
        await fixture.SeedAsync(fixture.TenantId, entity);

        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> result = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "graph-after";
                order.Lines.Add(new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    TenantId = Guid.NewGuid(),
                });
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });

        Assert.True(result.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(result.Failure);
        Assert.Equal(DataTransactionState.RolledBack, fixture.UnitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("graph-before", persisted.Name);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void WriteRepositoryFailsClosedWithoutConcurrencyToken()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<UnversionedDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new UnversionedDbContext(
            options,
            store,
            [new TenantEntityMapping<UnversionedEntity, Guid>(entity => entity.TenantId)],
            []);
        var coordinator = new TenantScopeCoordinator<UnversionedEntity, Guid>(
            new UnversionedTenantResolver(),
            store);
        var unitOfWork = new EfUnitOfWork<UnversionedDbContext>(context);

        Assert.Throws<Mecesoft.Data.EntityFrameworkCore.Concurrency.ConcurrencyConfigurationException>(
            () => new EfWriteRepository<UnversionedDbContext, UnversionedEntity, Guid, Guid>(
                context,
                coordinator,
                unitOfWork,
                entity => entity.Id));
    }

    [Fact]
    public async Task DirectSaveChangesRejectsTenantAndProtectedFieldBypass()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "direct");
        await fixture.SeedAsync(fixture.TenantId, entity);
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);

        Order tracked = await fixture.Context.Orders.SingleAsync();
        fixture.AuthorizeWrite(TenantOperation.Update);
        tracked.TenantId = Guid.NewGuid();
        SaveChangesSecurityException tenantException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(tenantException.Failure);
        fixture.Context.ChangeTracker.Clear();

        tracked = await fixture.Context.Orders.SingleAsync();
        fixture.AuthorizeWrite(TenantOperation.Update);
        tracked.Id = Guid.NewGuid();
        SaveChangesSecurityException keyException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<MutationInvariantFailure>(keyException.Failure);
        fixture.Context.ChangeTracker.Clear();

        tracked = await fixture.Context.Orders.SingleAsync();
        fixture.AuthorizeWrite(TenantOperation.Update);
        tracked.IsDeleted = true;
        SaveChangesSecurityException softDeleteException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<MutationInvariantFailure>(softDeleteException.Failure);
        fixture.Context.ChangeTracker.Clear();

        tracked = await fixture.Context.Orders.SingleAsync();
        fixture.AuthorizeWrite(TenantOperation.Update);
        tracked.Version = 99;
        SaveChangesSecurityException tokenException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<MutationInvariantFailure>(tokenException.Failure);
        fixture.Context.ChangeTracker.Clear();

        fixture.AuthorizeWrite(TenantOperation.Create);
        fixture.Context.Orders.Add(NewOrder(Guid.NewGuid(), "direct-add"));
        SaveChangesSecurityException addException = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(addException.Failure);
        fixture.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(fixture.TenantId, persisted.TenantId);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task DirectSaveChangesRejectsCrossTenantChild()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Order entity = NewOrder(fixture.TenantId, "direct-graph");
        await fixture.SeedAsync(fixture.TenantId, entity);
        Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
        fixture.AuthorizeWrite(TenantOperation.Create);

        Order tracked = await fixture.Context.Orders.SingleAsync();
        fixture.Context.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = tracked.Id,
            TenantId = Guid.NewGuid(),
        });

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixture.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        verification.AuthorizeReadForTesting(fixture.TenantId);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AddedChildCannotReferenceAnotherTenantsParent()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(tenantA);

        Order parentB = NewOrder(tenantB, "parent-b");
        await fixture.SeedAsync(tenantB, parentB);
        Order parentA = NewOrder(tenantA, "parent-a");
        await fixture.SeedAsync(tenantA, parentA);

        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        fixture.AuthorizeWrite(TenantOperation.Create);
        fixture.Context.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = parentB.Id,
            TenantId = tenantA,
        });

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixture.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FabricatedAttachCannotUpdateAnotherTenantsRow()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixtureA = await WriteFixture.CreateAsync(database, tenantA);
        await using WriteFixture fixtureB = await WriteFixture.CreateAsync(database, tenantB);
        Order orderA = NewOrder(tenantA, "original");
        await fixtureA.SeedAsync(tenantA, orderA);

        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Update));
        fixtureB.AuthorizeWrite(TenantOperation.Update);
        var fabricated = new Order
        {
            Id = orderA.Id,
            TenantId = tenantB,
            Name = "hijacked",
            Version = 1,
        };
        fixtureB.Context.Attach(fabricated);
        fixtureB.Context.Entry(fabricated).State = EntityState.Modified;

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixtureB.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixtureB.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            database,
            fixtureA.Store,
            TestContexts.OrderTenantMapping());
        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("original", persisted.Name);
        Assert.Equal(tenantA, persisted.TenantId);
    }

    [Fact]
    public async Task FabricatedAttachCannotUpdateAnotherTenantsRowOnSyncSave()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixtureA = await WriteFixture.CreateAsync(database, tenantA);
        await using WriteFixture fixtureB = await WriteFixture.CreateAsync(database, tenantB);
        Order orderA = NewOrder(tenantA, "sync-original");
        await fixtureA.SeedAsync(tenantA, orderA);

        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Update));
        fixtureB.AuthorizeWrite(TenantOperation.Update);
        var fabricated = new Order
        {
            Id = orderA.Id,
            TenantId = tenantB,
            Name = "sync-hijacked",
            Version = 1,
        };
        fixtureB.Context.Attach(fabricated);
        fixtureB.Context.Entry(fabricated).State = EntityState.Modified;

        SaveChangesSecurityException exception = Assert.Throws<SaveChangesSecurityException>(
            () => fixtureB.Context.SaveChanges());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixtureB.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            database,
            fixtureA.Store,
            TestContexts.OrderTenantMapping());
        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("sync-original", persisted.Name);
        Assert.Equal(tenantA, persisted.TenantId);
    }

    [Fact]
    public async Task AddedChildCannotReferenceTrackedOtherTenantParent()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(tenantA);

        Order parentB = NewOrder(tenantB, "tracked-parent-b");
        await fixture.SeedAsync(tenantB, parentB);
        Order parentA = NewOrder(tenantA, "tracked-parent-a");
        await fixture.SeedAsync(tenantA, parentA);

        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        fixture.AuthorizeWrite(TenantOperation.Create);
        fixture.Context.Attach(parentB);
        fixture.Context.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = parentB.Id,
            TenantId = tenantA,
        });

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixture.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        fixture.Store.Clear();
        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AddedChildCannotUseFabricatedTrackedPrincipal()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixtureA = await WriteFixture.CreateAsync(database, tenantA);
        await using WriteFixture fixtureB = await WriteFixture.CreateAsync(database, tenantB);
        Order orderA = NewOrder(tenantA, "real-parent");
        await fixtureA.SeedAsync(tenantA, orderA);

        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Update));
        fixtureB.AuthorizeWrite(TenantOperation.Create);
        var fabricatedParent = new Order
        {
            Id = orderA.Id,
            TenantId = tenantB,
            Name = "fabricated-parent",
            Version = 1,
        };
        fixtureB.Context.Attach(fabricatedParent);
        fixtureB.Context.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = fabricatedParent.Id,
            TenantId = tenantB,
        });

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixtureB.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixtureB.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            database,
            fixtureA.Store,
            TestContexts.OrderTenantMapping());
        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Assert.Empty(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeletingChildWithoutTrackedPrincipalIsAllowedAfterDatabaseTenantCheck()
    {
        Guid tenantId = Guid.NewGuid();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(tenantId);
        Order order = NewOrder(tenantId, "delete-parent");
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            TenantId = tenantId,
        };
        order.Lines.Add(line);
        await fixture.SeedAsync(tenantId, order);

        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        fixture.AuthorizeWrite(TenantOperation.HardDelete);
        OrderLine tracked = await fixture.Context.OrderLines.SingleAsync();
        fixture.Context.OrderLines.Remove(tracked);
        await fixture.Context.SaveChangesAsync();

        Assert.Empty(await fixture.Context.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FabricatedDeleteCannotRemoveAnotherTenantsChild()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixtureA = await WriteFixture.CreateAsync(database, tenantA);
        await using WriteFixture fixtureB = await WriteFixture.CreateAsync(database, tenantB);
        Order orderA = NewOrder(tenantA, "delete-victim");
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderA.Id,
            TenantId = tenantA,
        };
        orderA.Lines.Add(line);
        await fixtureA.SeedAsync(tenantA, orderA);

        Assert.True(fixtureB.Store.TrySet(tenantB, TenantOperation.Update));
        fixtureB.AuthorizeWrite(TenantOperation.HardDelete);
        var fabricated = new OrderLine
        {
            Id = line.Id,
            OrderId = orderA.Id,
            TenantId = tenantB,
        };
        fixtureB.Context.Attach(fabricated);
        fixtureB.Context.Entry(fabricated).State = EntityState.Deleted;

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixtureB.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixtureB.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            database,
            fixtureA.Store,
            TestContexts.OrderTenantMapping());
        fixtureA.Store.Clear();
        Assert.True(fixtureA.Store.TrySet(tenantA, TenantOperation.Read));
        fixtureA.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        Assert.Single(await verification.OrderLines.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SameTenantReparentingWithTrackedPrincipalIsAllowed()
    {
        Guid tenantId = Guid.NewGuid();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(tenantId);
        Order first = NewOrder(tenantId, "first-parent");
        Order second = NewOrder(tenantId, "second-parent");
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = first.Id,
            TenantId = tenantId,
        };
        first.Lines.Add(line);
        await fixture.SeedAsync(tenantId, first, second);

        Assert.True(fixture.Store.TrySet(tenantId, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantId);
        fixture.AuthorizeWrite(TenantOperation.Update);
        OrderLine trackedLine = await fixture.Context.OrderLines.SingleAsync();
        Order trackedSecond = await fixture.Context.Orders.SingleAsync(order => order.Id == second.Id);
        trackedLine.OrderId = trackedSecond.Id;
        await fixture.Context.SaveChangesAsync();

        fixture.Context.ChangeTracker.Clear();
        OrderLine persisted = Assert.Single(await fixture.Context.OrderLines
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(second.Id, persisted.OrderId);
    }

    [Fact]
    public async Task CrossTenantReparentingIsRejected()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(tenantA);

        Order orderA = NewOrder(tenantA, "reparent-a");
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderA.Id,
            TenantId = tenantA,
        };
        orderA.Lines.Add(line);
        await fixture.SeedAsync(tenantA, orderA);

        Order orderB = NewOrder(tenantB, "reparent-b");
        await fixture.SeedAsync(tenantB, orderB);

        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        fixture.AuthorizeWrite(TenantOperation.Update);
        OrderLine tracked = await fixture.Context.OrderLines.SingleAsync();
        tracked.OrderId = orderB.Id;

        SaveChangesSecurityException exception = await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => fixture.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        fixture.Context.ChangeTracker.Clear();

        await using TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping());
        Assert.True(fixture.Store.TrySet(tenantA, TenantOperation.Read));
        fixture.Context.AuthorizeReadForTesting(tenantA);
        verification.AuthorizeReadForTesting(tenantA);
        OrderLine persisted = Assert.Single(await verification.OrderLines.AsNoTracking().ToListAsync());
        Assert.Equal(orderA.Id, persisted.OrderId);
    }

    [Fact]
    public async Task TenantResolverFailureExecutesNoSql()
    {
        var interceptor = new CapturingCommandInterceptor();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(
            Guid.NewGuid(),
            interceptor: interceptor);
        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        fixture.Resolver.Fail = true;
        interceptor.Reset();

        DataResult<Order> add = await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "no-sql"));
        Assert.True(add.IsFailure);
        Assert.Empty(interceptor.Commands);

        DataResult<EntityUpdateResult<Order>> update = await fixture.Repository.UpdateAsync(
            Guid.NewGuid(),
            new ConcurrencyToken("1"),
            NoOpMutation);
        Assert.True(update.IsFailure);
        Assert.Empty(interceptor.Commands);
        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task AddWithoutTransactionExecutesNoSql()
    {
        var interceptor = new CapturingCommandInterceptor();
        await using WriteFixture fixture = await WriteFixture.CreateAsync(
            Guid.NewGuid(),
            interceptor: interceptor);
        interceptor.Reset();

        DataResult<Order> result = await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "no-transaction"));

        Assert.True(result.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);

        DataResult<EntityUpdateResult<Order>> update = await fixture.Repository.UpdateAsync(
            Guid.NewGuid(),
            new ConcurrencyToken("1"),
            NoOpMutation);
        Assert.True(update.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(update.Failure);
        Assert.Empty(interceptor.Commands);
    }

    [Fact]
    public async Task RollbackAndDisposePreserveDataIntegrity()
    {
        await using WriteFixture fixture = await WriteFixture.CreateAsync(Guid.NewGuid());
        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Assert.True((await fixture.Repository.AddAsync(
            NewOrder(fixture.TenantId, "rolled-back-add"))).IsSuccess);
        Assert.True((await fixture.UnitOfWork.RollbackAsync()).IsSuccess);

        await using (TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping()))
        {
            fixture.Store.Clear();
            Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
            fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
            verification.AuthorizeReadForTesting(fixture.TenantId);
            Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
        }

        Order entity = NewOrder(fixture.TenantId, "dispose-before");
        await fixture.SeedAsync(fixture.TenantId, entity);
        Assert.True((await fixture.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<Order>> update = await fixture.Repository.UpdateAsync(
            entity.Id,
            new ConcurrencyToken("1"),
            (order, _) =>
            {
                order.Name = "dispose-after";
                return ValueTask.FromResult(DataResult.Succeeded(EntityMutationResult.Completed));
            });
        Assert.True(update.IsSuccess, update.Failure?.ToString());
        await fixture.UnitOfWork.DisposeAsync();

        await using (TestDbContext verification = TestContexts.Create(
            fixture.Database,
            fixture.Store,
            TestContexts.OrderTenantMapping()))
        {
            fixture.Store.Clear();
            Assert.True(fixture.Store.TrySet(fixture.TenantId, TenantOperation.Read));
            fixture.Context.AuthorizeReadForTesting(fixture.TenantId);
            verification.AuthorizeReadForTesting(fixture.TenantId);
            Order persisted = Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
            Assert.Equal("dispose-before", persisted.Name);
            Assert.Equal(1, persisted.Version);
        }
    }

    private static readonly EntityMutation<Order> NoOpMutation =
        static (_, _) => ValueTask.FromResult(
            DataResult.Succeeded(EntityMutationResult.Completed));

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

    private sealed class WriteFixture : IAsyncDisposable
    {
        private WriteFixture(
            SqliteTestDatabase database,
            TenantScopeStore store,
            TestDbContext context,
            EfUnitOfWork<TestDbContext> unitOfWork,
            WriteTenantResolver resolver,
            EfWriteRepository<TestDbContext, Order, Guid, Guid> repository)
        {
            Database = database;
            Store = store;
            Context = context;
            UnitOfWork = unitOfWork;
            Resolver = resolver;
            Repository = repository;
        }

        public SqliteTestDatabase Database { get; }

        public TenantScopeStore Store { get; }

        public TestDbContext Context { get; }

        public EfUnitOfWork<TestDbContext> UnitOfWork { get; }

        public WriteTenantResolver Resolver { get; }

        public EfWriteRepository<TestDbContext, Order, Guid, Guid> Repository { get; }

        public Guid TenantId => Resolver.TenantId;

        public void AuthorizeWrite(TenantOperation operation)
        {
            Store.AuthorizeWriteForTesting(TenantId, operation);
        }

        public static async Task<WriteFixture> CreateAsync(
            Guid tenantId,
            bool graphInScope = true,
            CapturingCommandInterceptor? interceptor = null)
        {
            SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
            return await CreateAsync(database, tenantId, graphInScope, interceptor);
        }

        public static async Task<WriteFixture> CreateAsync(
            SqliteTestDatabase database,
            Guid tenantId,
            bool graphInScope = true,
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
            var resolver = new WriteTenantResolver(tenantId) { GraphInScope = graphInScope };
            var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
            var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
            var repository = new EfWriteRepository<TestDbContext, Order, Guid, Guid>(
                context,
                coordinator,
                unitOfWork,
                order => order.Id);

            return new WriteFixture(
                database,
                store,
                context,
                unitOfWork,
                resolver,
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

    private sealed class WriteTenantResolver(Guid tenantId) : ITenantAccessResolver<Order, Guid>
    {
        public Guid TenantId { get; } = tenantId;

        public bool Fail { get; set; }

        public bool GraphInScope { get; init; } = true;

        public bool CheckLines { get; set; } = true;

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
            bool linesInScope = !CheckLines || root.Lines.All(line => line.TenantId == TenantId);
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    GraphInScope
                    && root.TenantId == TenantId
                    && linesInScope));
        }
    }

    private sealed class UnversionedEntity : ITenantEntity<Guid>
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class UnversionedDbContext : MecesoftDbContext
    {
        public UnversionedDbContext(
            DbContextOptions<UnversionedDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<UnversionedEntity> Items => Set<UnversionedEntity>();

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UnversionedEntity>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class UnversionedTenantResolver : ITenantAccessResolver<UnversionedEntity, Guid>
    {
        public Expression<Func<UnversionedEntity, Guid>> TenantIdSelector
            => entity => entity.TenantId;

        public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(Guid.NewGuid(), operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            UnversionedEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }
}
