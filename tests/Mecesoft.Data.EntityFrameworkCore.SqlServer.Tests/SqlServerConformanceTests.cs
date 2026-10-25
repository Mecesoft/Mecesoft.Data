using System.Data.Common;
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

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[Collection("SqlServer")]
public sealed class SqlServerConformanceTests
{
    private readonly SqlServerTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerConformanceTests(
        SqlServerTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SqlServerFact]
    public async Task GuidTenantReadAndAddIsolation()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> addA = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "A"));
        Assert.True(addA.IsSuccess, addA.Failure?.ToString());
        Assert.NotEmpty(addA.Value.Version);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> addB = await scopeB.WriteRepository.AddAsync(
            NewOrder(tenantB, "B"));
        Assert.True(addB.IsSuccess, addB.Failure?.ToString());
        Assert.NotEmpty(addB.Value.Version);
        Assert.True((await scopeB.UnitOfWork.CommitAsync()).IsSuccess);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> listA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(listA.IsSuccess, listA.Failure?.ToString());
        SqlOrder persistedA = Assert.Single(listA.Value);
        Assert.Equal(addA.Value.Id, persistedA.Id);
        Assert.Equal(tenantA, persistedA.TenantId);

        scopeB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> listB =
            await scopeB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(listB.IsSuccess, listB.Failure?.ToString());
        SqlOrder persistedB = Assert.Single(listB.Value);
        Assert.Equal(addB.Value.Id, persistedB.Id);
        Assert.Equal(tenantB, persistedB.TenantId);

        DataResult<SqlOrder> crossTenant = await scopeA.ReadRepository.GetByIdAsync(addB.Value.Id);
        Assert.True(crossTenant.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenant.Failure);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<SqlOrder>> crossUpdate =
            await scopeB.WriteRepository.UpdateAsync(
                addA.Value.Id,
                SqlServerTestScope.Token(addA.Value),
                NoOpMutation);
        Assert.True(crossUpdate.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossUpdate.Failure);
        Assert.True((await scopeB.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [SqlServerFact]
    public async Task RowVersionIsProviderManagedAndStaleTokenIsRejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantId);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "rowversion"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        ConcurrencyToken token1 = SqlServerTestScope.Token(added.Value);
        byte[] version1 = added.Value.Version.ToArray();
        Assert.NotEmpty(version1);
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<SqlOrder>> updated =
            await scope.WriteRepository.UpdateAsync(
                added.Value.Id,
                token1,
                (order, _) =>
                {
                    order.Name = "rowversion-updated";
                    return ValueTask.FromResult(
                        DataResult.Succeeded(EntityMutationResult.Completed));
                });
        Assert.True(updated.IsSuccess, updated.Failure?.ToString());
        Assert.NotEqual(token1.Value, updated.Value.ConcurrencyToken.Value);
        Assert.NotEqual(
            Convert.ToBase64String(version1),
            updated.Value.ConcurrencyToken.Value);
        Assert.NotEmpty(updated.Value.Entity.Version);
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<SqlOrder>> stale =
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

    [SqlServerFact]
    public async Task ConcurrentRowVersionConflictRaisesDbUpdateConcurrencyException()
    {
        Guid tenantId = Guid.NewGuid();
        await using var first = _fixture.CreateScope(tenantId);
        await using var second = _fixture.CreateScope(tenantId);

        Assert.True((await first.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await first.WriteRepository.AddAsync(
            NewOrder(tenantId, "rowversion-race"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await first.UnitOfWork.CommitAsync()).IsSuccess);
        first.Context.ChangeTracker.Clear();
        second.Context.ChangeTracker.Clear();

        first.SetTenant(tenantId, TenantOperation.Read);
        second.SetTenant(tenantId, TenantOperation.Read);
        SqlOrder firstOrder = await first.Context.Orders.SingleAsync(
            order => order.Id == added.Value.Id);
        SqlOrder secondOrder = await second.Context.Orders.SingleAsync(
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
        SqlOrder persisted = await first.Context.Orders
            .AsNoTracking()
            .SingleAsync(order => order.Id == added.Value.Id);
        Assert.Equal("second-writer", persisted.Name);
    }

    [SqlServerFact]
    public async Task SoftDeleteAndRestoreRespectTenantAndRowVersion()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> addA = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "soft-delete-A"));
        Assert.True(addA.IsSuccess, addA.Failure?.ToString());
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> addB = await scopeB.WriteRepository.AddAsync(
            NewOrder(tenantB, "soft-delete-B"));
        Assert.True(addB.IsSuccess, addB.Failure?.ToString());
        Assert.True((await scopeB.UnitOfWork.CommitAsync()).IsSuccess);

        ConcurrencyToken beforeDelete = SqlServerTestScope.Token(addA.Value);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> deleted = await scopeA.SoftDeleteRepository.SoftDeleteAsync(
            addA.Value.Id,
            beforeDelete);
        Assert.True(deleted.IsSuccess, deleted.Failure?.ToString());
        Assert.True(deleted.Value.IsDeleted);
        Assert.NotNull(deleted.Value.DeletedAtUtc);
        Assert.NotEqual(beforeDelete.Value, SqlServerTestScope.Token(deleted.Value).Value);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> crossDelete = await scopeB.SoftDeleteRepository.SoftDeleteAsync(
            addA.Value.Id,
            SqlServerTestScope.Token(deleted.Value));
        Assert.True(crossDelete.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossDelete.Failure);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> activeA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(activeA.IsSuccess, activeA.Failure?.ToString());
        Assert.Empty(activeA.Value);
        scopeA.SetTenant(tenantA, TenantOperation.Read);
        List<SqlOrder> deletedRows;
        using (SoftDeleteReadAuthorization.Begin(
            tenantA,
            SoftDeleteOperation.SoftDelete))
        {
            deletedRows = await scopeA.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        SqlOrder deletedRow = Assert.Single(deletedRows);
        Assert.True(deletedRow.IsDeleted);

        scopeB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> activeB =
            await scopeB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(activeB.IsSuccess, activeB.Failure?.ToString());
        Assert.Single(activeB.Value);

        ConcurrencyToken beforeRestore = SqlServerTestScope.Token(deleted.Value);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> restored = await scopeA.SoftDeleteRepository.RestoreAsync(
            addA.Value.Id,
            beforeRestore);
        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.False(restored.Value.IsDeleted);
        Assert.Null(restored.Value.DeletedAtUtc);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> crossRestore = await scopeB.SoftDeleteRepository.RestoreAsync(
            addA.Value.Id,
            SqlServerTestScope.Token(restored.Value));
        Assert.True(crossRestore.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossRestore.Failure);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> restoredA =
            await scopeA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(restoredA.IsSuccess, restoredA.Failure?.ToString());
        Assert.Single(restoredA.Value);
    }

    [SqlServerFact]
    public async Task AddedChildWithCrossTenantForeignKeyIsRejected()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantA);

        scope.SetTenant(tenantB, TenantOperation.Create);
        SqlOrder orderB = NewOrder(tenantB, "parent-B");
        scope.Context.Orders.Add(orderB);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantA, TenantOperation.Create);
        SqlOrder orderA = NewOrder(tenantA, "parent-A");
        scope.Context.Orders.Add(orderA);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantA, TenantOperation.Create);
        scope.Context.OrderLines.Add(new SqlOrderLine
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

    [SqlServerFact]
    public async Task FabricatedAttachCannotUpdateAnotherTenantsRow()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scopeA = _fixture.CreateScope(tenantA);
        await using var scopeB = _fixture.CreateScope(tenantB);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await scopeA.WriteRepository.AddAsync(
            NewOrder(tenantA, "original"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        byte[] version = added.Value.Version.ToArray();
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        scopeB.SetTenant(tenantB, TenantOperation.Update);
        var fabricated = new SqlOrder
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
        SqlOrder persisted = Assert.Single(
            await scopeA.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("original", persisted.Name);
        Assert.Equal(tenantA, persisted.TenantId);
    }

    [SqlServerFact]
    public async Task CommitPersistsAndRollbackDiscards()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(tenantId);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> committed = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "committed"));
        Assert.True(committed.IsSuccess, committed.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> rolledBack = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "rolled-back"));
        Assert.True(rolledBack.IsSuccess, rolledBack.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);

        scope.SetTenant(tenantId, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> rows =
            await scope.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(rows.IsSuccess, rows.Failure?.ToString());
        SqlOrder persisted = Assert.Single(rows.Value);
        Assert.Equal("committed", persisted.Name);
    }

    [SqlServerFact]
    public async Task CommitParticipantCannotChangeTenantScope()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(
            tenantA,
            (context, store) => [new TenantChangingParticipant(store, context, tenantB)]);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        scope.SetTenant(tenantA, TenantOperation.Create);
        DataResult<SqlOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantA, "participant-A"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());

        DataResult<UnitOfWorkCommit> commit = await scope.UnitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<AuthorizationFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, scope.UnitOfWork.CurrentTransaction!.State);
        Assert.Equal(tenantA, scope.Store.TenantId);

        await using var verifyB = _fixture.CreateScope(tenantB);
        verifyB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> bRows =
            await verifyB.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(bRows.IsSuccess, bRows.Failure?.ToString());
        Assert.Empty(bRows.Value);

        await using var verifyA = _fixture.CreateScope(tenantA);
        verifyA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<SqlOrder>> aRows =
            await verifyA.ReadRepository.ListAsync(new AllOrdersSpecification());
        Assert.True(aRows.IsSuccess, aRows.Failure?.ToString());
        Assert.Empty(aRows.Value);
    }

    [SqlServerFact]
    public async Task DateTimeOffsetRoundTrips()
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

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "datetimeoffset", createdAt));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        scope.SetTenant(tenantId, TenantOperation.Read);
        SqlOrder persisted = Assert.Single(
            await scope.Context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(createdAt, persisted.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(3), persisted.CreatedAt.Offset);
    }

    [SqlServerFact]
    public async Task IsolationLevelsRespectSqlServerSupport()
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
        ];

        foreach (DataIsolationLevel level in supportedLevels)
        {
            DataResult<IDataTransaction> begin =
                await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions(level));
            Assert.True(begin.IsSuccess, $"{level}: {begin.Failure}");
            Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);
        }

        Guid snapshotTenant = Guid.NewGuid();
        scope.SetTenant(snapshotTenant, TenantOperation.Create);
        scope.Context.Orders.Add(NewOrder(snapshotTenant, "snapshot-seed"));
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();
        scope.SetTenant(snapshotTenant, TenantOperation.Read);

        int snapshotState = await GetSnapshotIsolationStateAsync(scope.Context);
        DataResult<IDataTransaction> snapshot =
            await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions(DataIsolationLevel.Snapshot));

        _output.WriteLine(
            $"snapshot_isolation_state={snapshotState}; begin success={snapshot.IsSuccess}");
        if (snapshot.IsFailure)
        {
            Assert.False(snapshotState == 1);
            Assert.IsType<ValidationFailure>(snapshot.Failure);
            return;
        }

        bool queryFailed = false;
        try
        {
            _ = await scope.Context.Orders.AsNoTracking().AnyAsync();
        }
        catch (DbException)
        {
            queryFailed = true;
        }

        _output.WriteLine(
            $"snapshot_isolation_state={snapshotState}; query failed={queryFailed}");
        if (snapshotState == 1)
        {
            Assert.False(
                queryFailed,
                "Snapshot query failed even though snapshot isolation is enabled.");
        }
        else
        {
            Assert.True(
                queryFailed,
                "Snapshot query succeeded even though snapshot isolation is disabled.");
        }

        DataResult<TransactionCompletion> snapshotRollback =
            await scope.UnitOfWork.RollbackAsync();
        Assert.True(snapshotRollback.IsSuccess, snapshotRollback.Failure?.ToString());
    }

    [SqlServerFact]
    public async Task ProviderUniqueViolationMapsToPersistenceFailure()
    {
        Guid tenantId = Guid.NewGuid();
        await using var first = _fixture.CreateScope(tenantId);
        await using var duplicate = _fixture.CreateScope(tenantId);

        Assert.True((await first.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await first.WriteRepository.AddAsync(
            NewOrder(tenantId, "unique"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await first.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True((await duplicate.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        var conflicting = new SqlOrder
        {
            Id = added.Value.Id,
            TenantId = tenantId,
            Name = "duplicate",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        DataResult<SqlOrder> duplicateResult =
            await duplicate.WriteRepository.AddAsync(conflicting);

        Assert.True(duplicateResult.IsFailure);
        Assert.IsType<PersistenceFailure>(duplicateResult.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            duplicate.UnitOfWork.CurrentTransaction!.State);
    }

    [SqlServerFact]
    public async Task CommitParticipantFailureRollsBack()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateScope(
            tenantId,
            (_, _) => [new ThrowingParticipant()]);

        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<SqlOrder> added = await scope.WriteRepository.AddAsync(
            NewOrder(tenantId, "participant-failure"));
        Assert.True(added.IsSuccess, added.Failure?.ToString());

        DataResult<UnitOfWorkCommit> commit = await scope.UnitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<PersistenceFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, scope.UnitOfWork.CurrentTransaction!.State);

        scope.SetTenant(tenantId, TenantOperation.Read);
        Assert.Empty(await scope.Context.Orders.AsNoTracking().ToListAsync());
    }

    private static async Task<int> GetSnapshotIsolationStateAsync(
        SqlServerTestDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqlOrder NewOrder(
        Guid tenantId,
        string name,
        DateTimeOffset? createdAt = null)
    {
        return new SqlOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
    }

    private static readonly EntityMutation<SqlOrder> NoOpMutation =
        static (_, _) => ValueTask.FromResult(
            DataResult.Succeeded(EntityMutationResult.Completed));

    private sealed class AllOrdersSpecification : ISpecification<SqlOrder>
    {
        public Expression<Func<SqlOrder, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<SqlOrder>> Sorts => [];
    }
}
