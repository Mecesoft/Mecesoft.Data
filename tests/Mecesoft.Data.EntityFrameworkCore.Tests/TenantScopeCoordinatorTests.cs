using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class TenantScopeCoordinatorTests
{
    [Fact]
    public async Task SuccessfulResolutionSetsStoreAndRunsQuery()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, NewOrder(tenantA, "A"));
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, NewOrder(tenantB, "B"));
        context.ChangeTracker.Clear();
        store.Clear();

        var resolver = new FakeTenantResolver(tenantA);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);

        DataResult<int> result = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders
                .AsNoTracking()
                .CountAsync(cancellationToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
        Assert.Equal(tenantA, store.TenantId);
        Assert.Equal(TenantOperation.Read, store.Operation);
    }

    [Fact]
    public async Task ResolverFailureDoesNotRunQuery()
    {
        var store = new TenantScopeStore();
        var resolver = new FakeTenantResolver(Guid.NewGuid()) { Fail = true };
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        bool queried = false;

        DataResult<int> result = await coordinator.ExecuteAsync(
            TenantOperation.Read,
            _ =>
            {
                queried = true;
                return ValueTask.FromResult(1);
            });

        Assert.True(result.IsFailure);
        Assert.IsType<TenantUnavailableFailure>(result.Failure);
        Assert.False(queried);
        Assert.False(store.HasTenant);
    }

    [Fact]
    public async Task ConfigurationFailureDoesNotRunQuery()
    {
        var store = new TenantScopeStore();
        var resolver = new FakeTenantResolver(Guid.NewGuid())
        {
            ConfigurationFailure = new TenantConfigurationFailure("invalid tenant selector"),
        };
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        bool queried = false;

        DataResult<int> result = await coordinator.ExecuteAsync(
            TenantOperation.Read,
            _ =>
            {
                queried = true;
                return ValueTask.FromResult(1);
            });

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
        Assert.False(queried);
    }

    [Fact]
    public async Task ConflictingTenantScopeDoesNotRunQuery()
    {
        var store = new TenantScopeStore();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Assert.True(store.TrySet(tenantB, TenantOperation.Read));

        var resolver = new FakeTenantResolver(tenantA);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        bool queried = false;

        DataResult<int> result = await coordinator.ExecuteAsync(
            TenantOperation.Read,
            _ =>
            {
                queried = true;
                return ValueTask.FromResult(1);
            });

        Assert.True(result.IsFailure);
        Assert.IsType<AuthorizationFailure>(result.Failure);
        Assert.False(queried);
        Assert.Equal(tenantB, store.TenantId);
    }

    [Fact]
    public async Task MutationValidationRejectsProtectedTenantChange()
    {
        Guid tenantId = Guid.NewGuid();
        var store = new TenantScopeStore();
        var resolver = new FakeTenantResolver(tenantId);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        var entity = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "before",
        };
        var guard = coordinator.CaptureMutation(entity, order => order.Id);

        entity.TenantId = Guid.NewGuid();
        DataResult<bool> result = await coordinator.ValidateMutationAsync(
            entity,
            guard,
            TenantOperation.Update);

        Assert.True(result.IsFailure);
        Assert.IsType<MutationInvariantFailure>(result.Failure);
    }

    [Fact]
    public async Task MutationValidationRejectsOutOfScopeGraph()
    {
        Guid tenantId = Guid.NewGuid();
        var store = new TenantScopeStore();
        var resolver = new FakeTenantResolver(tenantId) { GraphInScope = false };
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        var entity = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "graph",
        };
        var guard = coordinator.CaptureMutation(entity, order => order.Id);

        DataResult<bool> result = await coordinator.ValidateMutationAsync(
            entity,
            guard,
            TenantOperation.Update);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(result.Failure);
    }

    [Fact]
    public async Task MutationValidationAcceptsInScopeEntityAndGraph()
    {
        Guid tenantId = Guid.NewGuid();
        var store = new TenantScopeStore();
        var resolver = new FakeTenantResolver(tenantId);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        var entity = new Order
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "before",
        };
        var guard = coordinator.CaptureMutation(entity, order => order.Id);

        entity.Name = "after";
        DataResult<bool> result = await coordinator.ValidateMutationAsync(
            entity,
            guard,
            TenantOperation.Update);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(tenantId, store.TenantId);
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

    private sealed class FakeTenantResolver(Guid tenantId) : ITenantAccessResolver<Order, Guid>
    {
        public Guid TenantId { get; } = tenantId;

        public bool Fail { get; init; }

        public bool GraphInScope { get; init; } = true;

        public DataFailure? ConfigurationFailure { get; init; }

        public Expression<Func<Order, Guid>> TenantIdSelector => order => order.TenantId;

        public DataResult<bool> ValidateConfiguration()
        {
            return ConfigurationFailure is null
                ? DataResult.Succeeded(true)
                : DataResult.Failed<bool>(ConfigurationFailure!);
        }

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
            return ValueTask.FromResult(
                DataResult.Succeeded(GraphInScope && root.TenantId == TenantId));
        }
    }
}
