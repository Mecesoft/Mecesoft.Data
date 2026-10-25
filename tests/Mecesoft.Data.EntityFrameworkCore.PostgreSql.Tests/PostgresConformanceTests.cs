using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[Collection("PostgreSql")]
public sealed class PostgresConformanceTests
{
    private readonly PostgresTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresConformanceTests(
        PostgresTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [PostgresFact]
    public async Task GuidTenantReadAndAddIsolation()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> addA = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "A"));
        Assert.True(addA.IsSuccess, addA.Failure?.ToString());
        Assert.NotEqual(Guid.Empty, addA.Value.Version);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> addB = await scopeB.WriteRepository.AddAsync(
            NewOrder(tenantB, "B"));
        Assert.True(addB.IsSuccess, addB.Failure?.ToString());
        Assert.NotEqual(Guid.Empty, addB.Value.Version);
        Assert.True((await scopeB.UnitOfWork.CommitAsync()).IsSuccess);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> listA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(listA.IsSuccess, listA.Failure?.ToString());
        PgOrder persistedA = Assert.Single(listA.Value);
        Assert.Equal(addA.Value.Id, persistedA.Id);
        Assert.Equal(tenantA, persistedA.TenantId);

        scopeB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> listB =
            await scopeB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(listB.IsSuccess, listB.Failure?.ToString());
        PgOrder persistedB = Assert.Single(listB.Value);
        Assert.Equal(addB.Value.Id, persistedB.Id);
        Assert.Equal(tenantB, persistedB.TenantId);

        DataResult<PgOrder> crossTenant = await scopeA.ReadRepository.GetByIdAsync(addB.Value.Id);
        Assert.True(crossTenant.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenant.Failure);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<PgOrder>> crossUpdate =
            await scopeB.WriteRepository.UpdateAsync(
                addA.Value.Id,
                PostgresTestScope.Token(addA.Value),
                NoOpMutation);
        Assert.True(crossUpdate.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossUpdate.Failure);
        Assert.True((await scopeB.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [PostgresFact]
    public async Task ConcurrencyTokenRotatesAndStaleTokenIsRejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantId);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "concurrency"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        ConcurrencyToken token1 = PostgresTestScope.Token(added.Value);
        Guid version1 = added.Value.Version;
        Assert.NotEqual(Guid.Empty, version1);
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<PgOrder>> updated =
            await scope.WriteRepository.UpdateAsync(
                added.Value.Id,
                token1,
                (order, _) =>
                {
                    order.Name = "concurrency-updated";
                    return ValueTask.FromResult(
                        DataResult.Succeeded(EntityMutationResult.Completed));
                });
        Assert.True(updated.IsSuccess, updated.Failure?.ToString());
        Assert.NotEqual(token1.Value, updated.Value.ConcurrencyToken.Value);
        Assert.NotEqual(version1, updated.Value.Entity.Version);
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<PgOrder>> stale =
            await scope.WriteRepository.UpdateAsync(
                added.Value.Id,
                token1,
                NoOpMutation);
        Assert.True(stale.IsFailure);
        Assert.IsType<ConcurrencyFailure<Guid>>(stale.Failure);
        Assert.Equal(
            DataTransactionState.Active,
            scope.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [PostgresFact]
    public async Task ConcurrentUpdateConflictRaisesDbUpdateConcurrencyException()
    {
        Guid tenantId = Guid.NewGuid();
        await using var first = _fixture.CreateScope(tenantId);
        await using var second = _fixture.CreateScope(tenantId);

        Assert.True((await first.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await first.WriteRepository.AddAsync(
            NewOrder(tenantId, "concurrency-race"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await first.UnitOfWork.CommitAsync()).IsSuccess);
        first.Context.ChangeTracker.Clear();
        second.Context.ChangeTracker.Clear();

        first.SetTenant(tenantId, TenantOperation.Read);
        second.SetTenant(tenantId, TenantOperation.Read);
        PgOrder firstOrder = await first.Context.Orders.SingleAsync(
            order => order.Id == added.Value.Id);
        PgOrder secondOrder = await second.Context.Orders.SingleAsync(
            order => order.Id == added.Value.Id);

        secondOrder.Name = "second-writer";
        second.SetTenant(tenantId, TenantOperation.Update);
        await second.Context.SaveChangesAsync();

        firstOrder.Name = "first-writer";
        first.SetTenant(tenantId, TenantOperation.Update);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => first.Context.SaveChangesAsync());
        first.Context.ChangeTracker.Clear();

        first.SetTenant(tenantId, TenantOperation.Read);
        PgOrder persisted = await first.Context.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == added.Value.Id);
        Assert.Equal("second-writer", persisted.Name);
    }

    [PostgresFact]
    public async Task SoftDeleteAndRestoreRespectTenantAndConcurrencyToken()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> addA = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "soft-delete-A"));
        Assert.True(addA.IsSuccess, addA.Failure?.ToString());
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> addB = await scopeB.WriteRepository.AddAsync(
            NewOrder(tenantB, "soft-delete-B"));
        Assert.True(addB.IsSuccess, addB.Failure?.ToString());
        Assert.True((await scopeB.UnitOfWork.CommitAsync()).IsSuccess);

        ConcurrencyToken beforeDelete = PostgresTestScope.Token(addA.Value);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> deleted = await scopeA.SoftDeleteRepository.SoftDeleteAsync(
            addA.Value.Id,
            beforeDelete);
        Assert.True(deleted.IsSuccess, deleted.Failure?.ToString());
        Assert.True(deleted.Value.IsDeleted);
        Assert.NotNull(deleted.Value.DeletedAtUtc);
        Assert.NotEqual(beforeDelete.Value, PostgresTestScope.Token(deleted.Value).Value);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> crossDelete = await scopeB.SoftDeleteRepository.SoftDeleteAsync(
            addA.Value.Id,
            PostgresTestScope.Token(deleted.Value));
        Assert.True(crossDelete.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossDelete.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            scopeB.UnitOfWork.CurrentTransaction!.State);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> activeA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(activeA.IsSuccess, activeA.Failure?.ToString());
        Assert.Empty(activeA.Value);
        scopeA.SetTenant(tenantA, TenantOperation.Read);
        List<PgOrder> deletedRows;
        using (SoftDeleteReadAuthorization.Begin(
            tenantA,
            SoftDeleteOperation.SoftDelete))
        {
            deletedRows = await scopeA.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        PgOrder deletedRow = Assert.Single(deletedRows);
        Assert.True(deletedRow.IsDeleted);

        scopeB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> activeB =
            await scopeB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(activeB.IsSuccess, activeB.Failure?.ToString());
        Assert.Single(activeB.Value);

        ConcurrencyToken beforeRestore = PostgresTestScope.Token(deleted.Value);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> restored = await scopeA.SoftDeleteRepository.RestoreAsync(
            addA.Value.Id,
            beforeRestore);
        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.False(restored.Value.IsDeleted);
        Assert.Null(restored.Value.DeletedAtUtc);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> crossRestore = await scopeB.SoftDeleteRepository.RestoreAsync(
            addA.Value.Id,
            PostgresTestScope.Token(restored.Value));
        Assert.True(crossRestore.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossRestore.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            scopeB.UnitOfWork.CurrentTransaction!.State);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> restoredA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(restoredA.IsSuccess, restoredA.Failure?.ToString());
        Assert.Single(restoredA.Value);
    }

    [PostgresFact]
    public async Task AddedChildWithCrossTenantForeignKeyIsRejected()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantA);

        scope.SetTenant(tenantB, TenantOperation.Create);
        PgOrder orderB = NewOrder(tenantB, "parent-B");
        scope.Context.Orders.Add(orderB);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantA, TenantOperation.Create);
        PgOrder orderA = NewOrder(tenantA, "parent-A");
        scope.Context.Orders.Add(orderA);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantA, TenantOperation.Create);
        scope.Context.OrderLines.Add(new PgOrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderB.Id,
            TenantId = tenantA,
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => scope.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantA, TenantOperation.Read);
        Assert.Empty(await scope.Context.OrderLines.AsNoTracking().ToListAsync());
    }

    [PostgresFact]
    public async Task FabricatedAttachCannotUpdateAnotherTenantsRow()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "original"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Guid version = added.Value.Version;
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        scopeB.SetTenant(tenantB, TenantOperation.Update);
        var fabricated = new PgOrder
        {
            Id = added.Value.Id,
            TenantId = tenantB,
            Name = "hijacked",
            CreatedAt = DateTimeOffset.UtcNow,
            Version = version,
        };
        scopeB.Context.Attach(fabricated);
        scopeB.Context.Entry(fabricated).State = EntityState.Modified;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => scopeB.Context.SaveChangesAsync());
        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        scopeB.Context.ChangeTracker.Clear();

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        PgOrder persisted = Assert.Single(
            await scopeA.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("original", persisted.Name);
        Assert.Equal(tenantA, persisted.TenantId);
    }

    [PostgresFact]
    public async Task CommitPersistsAndRollbackDiscards()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantId);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> committed = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "committed"));
        Assert.True(committed.IsSuccess, committed.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> rolledBack = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "rolled-back"));
        Assert.True(rolledBack.IsSuccess, rolledBack.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);

        scope.SetTenant(tenantId, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> rows =
            await scope.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(rows.IsSuccess, rows.Failure?.ToString());
        PgOrder persisted = Assert.Single(rows.Value);
        Assert.Equal("committed", persisted.Name);
    }

    [PostgresFact]
    public async Task CommitParticipantCannotChangeTenantScope()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(
            tenantA,
            (context, store) => [new PgTenantChangingParticipant(store, context, tenantB)]);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        scope.SetTenant(tenantA, TenantOperation.Create);
        DataResult<PgOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantA, "participant-A"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());

        DataResult<UnitOfWorkCommit> commit = await scope.UnitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<AuthorizationFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, scope.UnitOfWork.CurrentTransaction!.State);
        Assert.Equal(tenantA, scope.Store.TenantId);

        await using var verifyB = _fixture.CreateScope(tenantB);
        verifyB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> bRows =
            await verifyB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(bRows.IsSuccess, bRows.Failure?.ToString());
        Assert.Empty(bRows.Value);

        await using var verifyA = _fixture.CreateScope(tenantA);
        verifyA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<PgOrder>> aRows =
            await verifyA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(aRows.IsSuccess, aRows.Failure?.ToString());
        Assert.Empty(aRows.Value);
    }

    [PostgresFact]
    public async Task DateTimeOffsetRoundTripsAsUtcInstant()
    {
        Guid tenantId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(
            2026,
            10,
            25,
            12,
            34,
            56,
            TimeSpan.Zero);
        await using var scope = _fixture.CreateScope(tenantId);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "datetimeoffset", createdAt));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        scope.SetTenant(tenantId, TenantOperation.Read);
        PgOrder persisted = Assert.Single(
            await scope.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(createdAt, persisted.CreatedAt);
        Assert.Equal(TimeSpan.Zero, persisted.CreatedAt.Offset);
    }

    [PostgresFact]
    public async Task NonUtcDateTimeOffsetIsRejectedWithoutPersisting()
    {
        Guid tenantId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(
            2026,
            10,
            25,
            12,
            34,
            56,
            TimeSpan.FromHours(3));
        await using var scope = _fixture.CreateScope(tenantId);

        scope.SetTenant(tenantId, TenantOperation.Create);
        scope.Context.Orders.Add(NewOrder(tenantId, "non-utc-offset", createdAt));

        Exception? failure = await Record.ExceptionAsync(
            () => scope.Context.SaveChangesAsync());

        Assert.NotNull(failure);
        _output.WriteLine($"Non-UTC DateTimeOffset failure: {failure.GetType().Name}");
        Assert.Matches("(?i)offset|UTC", failure.ToString());
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantId, TenantOperation.Read);
        Assert.Empty(await scope.Context.Orders.AsNoTracking().ToListAsync());
    }

    [PostgresFact]
    public async Task IsolationLevelsRespectPostgresSupport()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantId);

        DataIsolationLevel[] supportedLevels =
        [
            DataIsolationLevel.ProviderDefault,
            DataIsolationLevel.ReadUncommitted,
            DataIsolationLevel.ReadCommitted,
            DataIsolationLevel.RepeatableRead,
            DataIsolationLevel.Serializable,
            DataIsolationLevel.Snapshot,
        ];

        foreach (DataIsolationLevel level in supportedLevels)
        {
            DataResult<IDataTransaction> begin =
                await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions(level));
            Assert.True(begin.IsSuccess, $"{level}: {begin.Failure}");
            Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);
        }
    }

    [PostgresFact]
    public async Task SnapshotLevelProvidesRepeatableReadSnapshot()
    {
        Guid tenantId = Guid.NewGuid();
        await using var reader = _fixture.CreateScope(tenantId);
        await using var writer = _fixture.CreateScope(tenantId);

        reader.SetTenant(tenantId, TenantOperation.Read);
        Assert.True((await reader.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions(DataIsolationLevel.Snapshot))).IsSuccess);

        int before = await reader.Context.Orders.AsNoTracking().CountAsync();
        Assert.Equal(0, before);

        Assert.True((await writer.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await writer.WriteRepository.AddAsync(
            NewOrder(tenantId, "snapshot-visible-later"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await writer.UnitOfWork.CommitAsync()).IsSuccess);

        int insideSnapshot = await reader.Context.Orders.AsNoTracking().CountAsync();
        Assert.Equal(0, insideSnapshot);
        Assert.True((await reader.UnitOfWork.RollbackAsync()).IsSuccess);

        reader.Context.ChangeTracker.Clear();
        reader.SetTenant(tenantId, TenantOperation.Read);
        int afterRollback = await reader.Context.Orders.AsNoTracking().CountAsync();
        Assert.Equal(1, afterRollback);

        _output.WriteLine(
            "PostgreSQL Snapshot maps to REPEATABLE READ and provided snapshot semantics.");
    }

    [PostgresFact]
    public async Task ProviderUniqueViolationMapsToPersistenceFailure()
    {
        Guid tenantId = Guid.NewGuid();
        await using var first = _fixture.CreateScope(tenantId);
        await using var duplicate = _fixture.CreateScope(tenantId);

        Assert.True((await first.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await first.WriteRepository.AddAsync(
            NewOrder(tenantId, "unique"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await first.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await duplicate.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        var conflicting = new PgOrder
        {
            Id = added.Value.Id,
            TenantId = tenantId,
            Name = "duplicate",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        DataResult<PgOrder> duplicateResult =
            await duplicate.WriteRepository.AddAsync(conflicting);

        Assert.True(duplicateResult.IsFailure);
        Assert.IsType<PersistenceFailure>(duplicateResult.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            duplicate.UnitOfWork.CurrentTransaction!.State);
    }

    [PostgresFact]
    public async Task CommitParticipantFailureRollsBack()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(
            tenantId,
            (_, _) => [new PgThrowingParticipant()]);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<PgOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "participant-failure"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());

        DataResult<UnitOfWorkCommit> commit = await scope.UnitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<PersistenceFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, scope.UnitOfWork.CurrentTransaction!.State);

        scope.SetTenant(tenantId, TenantOperation.Read);
        Assert.Empty(await scope.Context.Orders.AsNoTracking().ToListAsync());
    }

    private static PgOrder NewOrder(
        Guid tenantId,
        string name,
        DateTimeOffset? createdAt = null)
    {
        return new PgOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
    }

    private static readonly EntityMutation<PgOrder> NoOpMutation =
        static (_, _) => ValueTask.FromResult(
            DataResult.Succeeded(EntityMutationResult.Completed));

    private sealed class AllOrdersSpecification : ISpecification<PgOrder>
    {
        public Expression<Func<PgOrder, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<PgOrder>> Sorts => [];
    }
}
