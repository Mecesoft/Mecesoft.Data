using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class KeySelectorValidationTests
{
    [Fact]
    public async Task ReadRepositoryRejectsNonPrimaryKeySelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new OrderTenantResolver(),
            store);

        Assert.Throws<ArgumentException>(
            () => new EfReadRepository<TestDbContext, Order, string, Guid>(
                context,
                coordinator,
                order => order.Name));
    }

    [Fact]
    public async Task ReadRepositoryRejectsNestedKeySelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var coordinator = new TenantScopeCoordinator<OrderLine, Guid>(
            new OrderLineTenantResolver(),
            store);

        Assert.Throws<ArgumentException>(
            () => new EfReadRepository<TestDbContext, OrderLine, Guid, Guid>(
                context,
                coordinator,
                line => line.Order!.Id));
    }

    [Fact]
    public async Task WriteRepositoryRejectsNonPrimaryKeySelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new OrderTenantResolver(),
            store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.Throws<ArgumentException>(
            () => new EfWriteRepository<TestDbContext, Order, string, Guid>(
                context,
                coordinator,
                unitOfWork,
                order => order.Name));
    }

    [Fact]
    public async Task WriteRepositoryRejectsNestedKeySelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var coordinator = new TenantScopeCoordinator<OrderLine, Guid>(
            new OrderLineTenantResolver(),
            store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.Throws<ArgumentException>(
            () => new EfWriteRepository<TestDbContext, OrderLine, Guid, Guid>(
                context,
                coordinator,
                unitOfWork,
                line => line.Order!.Id));
    }

    [Fact]
    public async Task SoftDeleteRepositoryRejectsNonPrimaryKeySelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new OrderTenantResolver(),
            store);
        var unitOfWork = new EfUnitOfWork<TestDbContext>(context);

        Assert.Throws<ArgumentException>(
            () => new EfSoftDeleteRepository<TestDbContext, Order, string, Guid>(
                context,
                coordinator,
                unitOfWork,
                new AllowAllStringSoftDeleteAuthorizer(),
                order => order.Name));
    }

    private sealed class OrderTenantResolver : ITenantAccessResolver<Order, Guid>
    {
        private readonly Guid _tenantId = Guid.NewGuid();

        public Expression<Func<Order, Guid>> TenantIdSelector => order => order.TenantId;

        public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(_tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            Order root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == _tenantId));
        }
    }

    private sealed class OrderLineTenantResolver : ITenantAccessResolver<OrderLine, Guid>
    {
        private readonly Guid _tenantId = Guid.NewGuid();

        public Expression<Func<OrderLine, Guid>> TenantIdSelector => line => line.TenantId;

        public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(_tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            OrderLine root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == _tenantId));
        }
    }

    private sealed class AllowAllStringSoftDeleteAuthorizer : ISoftDeleteAuthorizer<Order, string, Guid>
    {
        public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
            SoftDeleteAuthorizationContext<Order, string, Guid> context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(context.Operation));
        }
    }
}
