using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class AuthorizationLifecycleSecurityTests
{
    [Fact]
    public async Task ResolveReadDoesNotAuthorizeTenantWrite()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<TenantScope<Guid>> read = await coordinator.ResolveAsync(
            TenantOperation.Read);

        Assert.True(read.IsSuccess);
        context.Orders.Add(NewOrder(tenantId, "read-then-write"));

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task WriteGrantDoesNotAuthorizeDirectRead()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<TenantScope<Guid>> create = await coordinator.ResolveAsync(
            TenantOperation.Create);

        Assert.True(create.IsSuccess);
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsyncReadClearsGrantAfterSuccess()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, store, tenantId, NewOrder(tenantId, "visible"));

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<int> result = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async token => await context.Orders.CountAsync(token));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsyncReadClearsGrantAfterException()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.ExecuteAsync<object>(
                context,
                TenantOperation.Read,
                _ => throw new InvalidOperationException("query failed")));

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsyncReadClearsGrantAfterCancellation()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await coordinator.ExecuteAsync<object>(
                context,
                TenantOperation.Read,
                _ => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task ExecuteAsyncWriteFailureClearsGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.ExecuteAsync<object>(
                TenantOperation.Create,
                _ => throw new InvalidOperationException("write failed")));

        context.Orders.Add(NewOrder(tenantId, "after-failure"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task ExecuteAsyncWriteCancellationClearsGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await coordinator.ExecuteAsync<object>(
                TenantOperation.Create,
                _ => throw new OperationCanceledException()));

        context.Orders.Add(NewOrder(tenantId, "after-cancellation"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task RepositoryReadClearsReadGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Order order = NewOrder(tenantId, "repository-read");
        await SeedAsync(context, store, tenantId, order);

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        var repository = new EfReadRepository<TestDbContext, Order, Guid, Guid>(
            context,
            coordinator,
            item => item.Id);
        DataResult<Order> result = await repository.GetByIdAsync(order.Id);

        Assert.True(result.IsSuccess);
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task DeferredQueryFailsAfterReadGrantCleanup()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, store, tenantId, NewOrder(tenantId, "deferred"));

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<IQueryable<Order>> result = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            _ => ValueTask.FromResult(
                context.Orders.AsNoTracking()));

        Assert.True(result.IsSuccess);
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => result.Value.ToListAsync());
    }

    [Fact]
    public async Task AsyncEnumerationFailsAfterReadGrantCleanup()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        await SeedAsync(context, store, tenantId, NewOrder(tenantId, "async"));

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<IAsyncEnumerable<Order>> result =
            await coordinator.ExecuteAsync(
                context,
                TenantOperation.Read,
                _ => ValueTask.FromResult(
                    context.Orders.AsNoTracking().AsAsyncEnumerable()));

        Assert.True(result.IsSuccess);
        await Assert.ThrowsAsync<TenantConfigurationException>(
            async () =>
            {
                await foreach (Order _ in result.Value)
                {
                }
            });
    }

    [Fact]
    public async Task CrossContextWriteGrantCannotBeReused()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();

        await using var contextA = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await using var contextB = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await contextA.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<TenantScope<Guid>> grant = await coordinator.ResolveAsync(
            contextA,
            TenantOperation.Create);
        Assert.True(grant.IsSuccess);

        contextB.Orders.Add(NewOrder(tenantId, "context-b"));
        await Assert.ThrowsAsync<SaveChangesSecurityException>(
            () => contextB.SaveChangesAsync());

        contextA.Orders.Add(NewOrder(tenantId, "context-a"));
        await contextA.SaveChangesAsync();
    }

    [Fact]
    public async Task ReadGrantCannotBeReusedByAnotherContext()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();

        await using var contextA = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await using var contextB = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await contextA.Database.EnsureCreatedAsync();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<int> read = await coordinator.ExecuteAsync(
            contextA,
            TenantOperation.Read,
            async token => await contextA.Orders.CountAsync(token));
        Assert.True(read.IsSuccess);

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => contextB.Orders.CountAsync());
    }

    [Fact]
    public async Task CommitParticipantCannotReplayConsumedGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Order first = NewOrder(tenantId, "first");
        Order second = NewOrder(tenantId, "second");
        var participant = new ReplayParticipant(context, second);
        await using var unitOfWork = new EfUnitOfWork<TestDbContext>(context, [participant]);
        Assert.True((await unitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Orders.Add(first);

        Assert.True((await unitOfWork.CommitAsync()).IsSuccess);
        Assert.True(participant.SecondWriteRejected);

        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantId);
        Order persisted = Assert.Single(
            await context.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(first.Id, persisted.Id);
    }

    [Fact]
    public async Task CommitClearsWriteGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        await using var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
        Assert.True((await unitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Orders.Add(NewOrder(tenantId, "committed"));
        Assert.True((await unitOfWork.CommitAsync()).IsSuccess);

        context.Orders.Add(NewOrder(tenantId, "after-commit"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task RollbackClearsWriteGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        await using var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
        Assert.True((await unitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Orders.Add(NewOrder(tenantId, "rolled-back"));
        Assert.True((await unitOfWork.RollbackAsync()).IsSuccess);

        context.Orders.Add(NewOrder(tenantId, "after-rollback"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    [Fact]
    public async Task DisposeClearsWriteGrant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        Guid tenantId = Guid.NewGuid();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);
        Assert.True((await unitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        context.Orders.Add(NewOrder(tenantId, "disposed"));
        await unitOfWork.DisposeAsync();

        context.Orders.Add(NewOrder(tenantId, "after-dispose"));
        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());
        Assert.IsType<AuthorizationFailure>(exception.Failure);
    }

    private static async Task SeedAsync(
        TestDbContext context,
        TenantScopeStore store,
        Guid tenantId,
        params Order[] orders)
    {
        await TestSeeds.SaveOrdersAsync(context, store, tenantId, orders);
        context.ChangeTracker.Clear();
        store.Clear();
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

    private sealed class ReplayParticipant(
        TestDbContext context,
        Order second) : IUnitOfWorkCommitParticipant
    {
        public bool SecondWriteRejected { get; private set; }

        public async ValueTask<DataResult<bool>> BeforeCommitAsync(
            IUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            await context.SaveChangesAsync(cancellationToken);
            context.Orders.Add(second);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (SaveChangesSecurityException exception)
                when (exception.Failure is AuthorizationFailure)
            {
                SecondWriteRejected = true;
                context.Entry(second).State = EntityState.Detached;
            }

            return DataResult.Succeeded(true);
        }
    }

    private sealed class FixedOrderResolver(Guid tenantId)
        : ITenantAccessResolver<Order, Guid>
    {
        public Expression<Func<Order, Guid>> TenantIdSelector
            => order => order.TenantId;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    new TenantScope<Guid>(tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            Order root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(root.TenantId == tenantId));
        }
    }
}
