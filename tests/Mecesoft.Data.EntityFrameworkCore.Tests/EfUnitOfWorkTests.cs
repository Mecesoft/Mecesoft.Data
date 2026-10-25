using System.Data.Common;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class EfUnitOfWorkTests
{
    [Fact]
    public async Task CommitPersistsChangesAndReportsAffectedEntries()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        DataResult<IDataTransaction> begin = await unitOfWork.BeginTransactionAsync(new TransactionOptions());
        Assert.True(begin.IsSuccess);
        Assert.True(unitOfWork.HasActiveTransaction);
        Assert.Equal(DataTransactionState.Active, unitOfWork.CurrentTransaction!.State);

        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "committed"));
        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsSuccess);
        Assert.True(commit.Value.AffectedEntries > 0);
        Assert.False(unitOfWork.HasActiveTransaction);
        Assert.Equal(DataTransactionState.Committed, unitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Single(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RollbackDiscardsChanges()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "rolled-back"));
        DataResult<TransactionCompletion> rollback = await unitOfWork.RollbackAsync();

        Assert.True(rollback.IsSuccess);
        Assert.Equal(DataTransactionState.RolledBack, rollback.Value.State);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CommitWithoutTransactionFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();
        DataResult<TransactionCompletion> rollback = await unitOfWork.RollbackAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(commit.Failure);
        Assert.True(rollback.IsFailure);
        Assert.IsType<TransactionRequiredFailure>(rollback.Failure);
    }

    [Fact]
    public async Task NestedBeginIsRejected()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        DataResult<IDataTransaction> second = await unitOfWork.BeginTransactionAsync(new TransactionOptions());

        Assert.True(second.IsFailure);
        Assert.IsType<TransactionStateFailure>(second.Failure);

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task CommitCancellationBeforeDispatchLeavesTransactionActive()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        Guid tenantId = Guid.NewGuid();
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "cancelled"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync(cancellation.Token);

        Assert.True(commit.IsFailure);
        Assert.IsType<ValidationFailure>(commit.Failure);
        Assert.True(unitOfWork.HasActiveTransaction);
        Assert.True((await unitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task DisposeRollsBackActiveTransaction()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "disposed"));
        await unitOfWork.DisposeAsync();

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CommitParticipantsRunInOrderInsideTheTransaction()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var calls = new List<string>();
        var first = new RecordingParticipant("first", calls);
        var audit = new AuditParticipant(context);
        var second = new RecordingParticipant("second", calls);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [first, audit, second]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "with-audit"));
        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsSuccess);
        Assert.Equal(["first", "second"], calls);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        Assert.Single(await verification.AuditEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ParticipantFailureAbortsAndRollsBack()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(
            context,
            [new FailingParticipant()]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "denied"));
        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<ValidationFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CommitFailureMarksIndeterminateAndPreservesState()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var interceptor = new FailingTransactionInterceptor();
        await using TestDbContext context = TestContexts.CreateWithTransactionInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        interceptor.FailCommit = true;
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "commit-failure"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<IndeterminateCommitFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.Indeterminate, unitOfWork.CurrentTransaction!.State);
        Assert.IsType<IndeterminateCommitFailure>((await unitOfWork.CommitAsync()).Failure);
        Assert.IsType<IndeterminateCommitFailure>((await unitOfWork.RollbackAsync()).Failure);

        await unitOfWork.DisposeAsync();
        Assert.NotNull(unitOfWork.CurrentTransaction);
        Assert.Equal(DataTransactionState.Indeterminate, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task RollbackFailureMarksIndeterminateAndPreservesState()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var interceptor = new FailingTransactionInterceptor();
        await using TestDbContext context = TestContexts.CreateWithTransactionInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        interceptor.FailRollback = true;
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "rollback-failure"));

        DataResult<TransactionCompletion> rollback = await unitOfWork.RollbackAsync();

        Assert.True(rollback.IsFailure);
        Assert.IsType<IndeterminateCommitFailure>(rollback.Failure);
        Assert.Equal(DataTransactionState.Indeterminate, unitOfWork.CurrentTransaction!.State);

        await unitOfWork.DisposeAsync();
        Assert.NotNull(unitOfWork.CurrentTransaction);
        Assert.Equal(DataTransactionState.Indeterminate, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task DisposeFailurePreservesIndeterminateState()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        var transaction = new FailingDbContextTransaction
        {
            FailRollback = true,
            FailDispose = true,
        };
        var unitOfWork = new EfUnitOfWork<TestDbContext>(
            context,
            null,
            _ => Task.FromResult<IDbContextTransaction>(transaction));

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        await unitOfWork.DisposeAsync();

        Assert.NotNull(unitOfWork.CurrentTransaction);
        Assert.Equal(DataTransactionState.Indeterminate, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task SaveChangesFailureRollsBackAndReturnsPersistenceFailure()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var interceptor = new FailingSaveChangesInterceptor();
        await using TestDbContext context = TestContexts.CreateWithSaveChangesInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "save-failure"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<PersistenceFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);
        Assert.True((await unitOfWork.CommitAsync()).IsFailure);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ParticipantFalseDenialRollsBackAndReturnsValidationFailure()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [new DenyingParticipant()]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "denied-false"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<ValidationFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ParticipantExceptionRollsBackAndReturnsPersistenceFailure()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [new ThrowingParticipant()]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "throwing-participant"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<PersistenceFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ParticipantCannotRecursivelyCommit()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var participant = new RecursiveCommitParticipant();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [participant]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "recursive-commit"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsSuccess, commit.Failure?.ToString());
        Assert.NotNull(participant.Result);
        Assert.IsType<TransactionStateFailure>(participant.Result!.Failure);
        Assert.Equal(DataTransactionState.Committed, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task ParticipantCannotRollbackOrBeginDuringCommit()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var participant = new StateChangingParticipant();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [participant]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "state-changing"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsSuccess, commit.Failure?.ToString());
        Assert.IsType<TransactionStateFailure>(participant.RollbackResult!.Failure);
        Assert.IsType<TransactionStateFailure>(participant.BeginResult!.Failure);
        Assert.Equal(DataTransactionState.Committed, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task ParticipantCannotDisposeDuringCommit()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantId = Guid.NewGuid();
        var participant = new DisposeAttemptParticipant();
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [participant]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantId);
        context.Orders.Add(NewOrder(tenantId, "dispose-participant"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsSuccess, commit.Failure?.ToString());
        Assert.True(participant.DisposeWasBlocked);
        Assert.Equal(DataTransactionState.Committed, unitOfWork.CurrentTransaction!.State);
    }

    [Fact]
    public async Task ParticipantCannotChangeTenantScopeDuringCommit()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = await CreateContextAsync(database, store);
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        var participant = new TenantScopeChangingParticipant(store, context, tenantB);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [participant]);

        Assert.True((await unitOfWork.BeginTransactionAsync(new TransactionOptions())).IsSuccess);
        SetTenant(store, tenantA);
        context.Orders.Add(NewOrder(tenantA, "tenant-a"));

        DataResult<UnitOfWorkCommit> commit = await unitOfWork.CommitAsync();

        Assert.True(commit.IsFailure);
        Assert.IsType<AuthorizationFailure>(commit.Failure);
        Assert.Equal(DataTransactionState.RolledBack, unitOfWork.CurrentTransaction!.State);
        Assert.Equal(tenantA, store.TenantId);

        await using TestDbContext verification = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        store.Clear();
        Assert.True(store.TrySet(tenantB, TenantOperation.Read));
        verification.AuthorizeReadForTesting(tenantB);
        Assert.Empty(await verification.Orders.AsNoTracking().ToListAsync());
    }

    private static void SetTenant(TenantScopeStore store, Guid tenantId)
    {
        store.Clear();
        if (!store.TrySet(tenantId, TenantOperation.Create))
        {
            throw new InvalidOperationException("The test tenant could not be set.");
        }

        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
    }

    private static async Task<TestDbContext> CreateContextAsync(
        SqliteTestDatabase database,
        TenantScopeStore store)
    {
        TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static Order NewOrder(Guid tenantId, string name)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
        };
    }

    private sealed class RecordingParticipant(string name, ICollection<string> calls)
        : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            calls.Add(name);
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class AuditParticipant(TestDbContext context) : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            context.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(),
                Message = "audit",
            });
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class FailingParticipant : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Failed<bool>(
                    new ValidationFailure("The participant denied the commit.")));
        }
    }

    private sealed class DenyingParticipant : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(false));
        }
    }

    private sealed class ThrowingParticipant : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The participant threw.");
        }
    }

    private sealed class RecursiveCommitParticipant : IUnitOfWorkCommitParticipant
    {
        public DataResult<UnitOfWorkCommit>? Result { get; private set; }

        public async ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            Result = await unitOfWork.CommitAsync(cancellationToken);
            return DataResult.Succeeded(true);
        }
    }

    private sealed class StateChangingParticipant : IUnitOfWorkCommitParticipant
    {
        public DataResult<TransactionCompletion>? RollbackResult { get; private set; }

        public DataResult<IDataTransaction>? BeginResult { get; private set; }

        public async ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            RollbackResult = await unitOfWork.RollbackAsync(cancellationToken);
            BeginResult = await unitOfWork.BeginTransactionAsync(
                new TransactionOptions(),
                cancellationToken);
            return DataResult.Succeeded(true);
        }
    }

    private sealed class DisposeAttemptParticipant : IUnitOfWorkCommitParticipant
    {
        public bool DisposeWasBlocked { get; private set; }

        public async ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await unitOfWork.DisposeAsync();
            }
            catch (InvalidOperationException)
            {
                DisposeWasBlocked = true;
            }

            return DataResult.Succeeded(true);
        }
    }

    private sealed class TenantScopeChangingParticipant(
        TenantScopeStore store,
        TestDbContext context,
        Guid otherTenant) : IUnitOfWorkCommitParticipant
    {
        public ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            store.Clear();
            _ = store.TrySet(otherTenant, TenantOperation.Create);
            context.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                TenantId = otherTenant,
                Name = "other-tenant",
            });
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class FailingTransactionInterceptor : DbTransactionInterceptor
    {
        public bool FailCommit { get; set; }

        public bool FailRollback { get; set; }

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            return FailCommit
                ? throw new InvalidOperationException("The commit failed.")
                : result;
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            return FailCommit
                ? throw new InvalidOperationException("The commit failed.")
                : ValueTask.FromResult(result);
        }

        public override InterceptionResult TransactionRollingBack(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            return FailRollback
                ? throw new InvalidOperationException("The rollback failed.")
                : result;
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            return FailRollback
                ? throw new InvalidOperationException("The rollback failed.")
                : ValueTask.FromResult(result);
        }
    }

    private sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The save failed.");
        }
    }

    private sealed class FailingDbContextTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public bool SupportsSavepoints => false;

        public bool FailCommit { get; set; }

        public bool FailRollback { get; set; }

        public bool FailDispose { get; set; }

        public void Commit()
        {
            if (FailCommit)
            {
                throw new InvalidOperationException("The commit failed.");
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (FailCommit)
            {
                throw new InvalidOperationException("The commit failed.");
            }

            return Task.CompletedTask;
        }

        public void Rollback()
        {
            if (FailRollback)
            {
                throw new InvalidOperationException("The rollback failed.");
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (FailRollback)
            {
                throw new InvalidOperationException("The rollback failed.");
            }

            return Task.CompletedTask;
        }

        public void CreateSavepoint(string name)
            => throw new NotSupportedException();

        public Task CreateSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void RollbackToSavepoint(string name)
            => throw new NotSupportedException();

        public Task RollbackToSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void ReleaseSavepoint(string name)
            => throw new NotSupportedException();

        public Task ReleaseSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
            if (FailDispose)
            {
                throw new InvalidOperationException("The dispose failed.");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (FailDispose)
            {
                throw new InvalidOperationException("The dispose failed.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
