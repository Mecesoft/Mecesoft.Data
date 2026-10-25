using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class EfReadRepositoryTests
{
    [Fact]
    public async Task TenantIsolationCoversGetByIdListFirstCountAndAny()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);

        var storeA = new TenantScopeStore();
        var storeB = new TenantScopeStore();
        await using TestDbContext contextA = TestContexts.Create(
            database,
            storeA,
            TestContexts.OrderTenantMapping());
        await using TestDbContext contextB = TestContexts.Create(
            database,
            storeB,
            TestContexts.OrderTenantMapping());

        var repositoryA = CreateRepository(contextA, storeA, seed.TenantA);
        var repositoryB = CreateRepository(contextB, storeB, seed.TenantB);

        DataResult<Order> getA = await repositoryA.GetByIdAsync(seed.AActive.Id);
        DataResult<Order> getBFromA = await repositoryA.GetByIdAsync(seed.BActive.Id);
        DataResult<Order> getDeletedA = await repositoryA.GetByIdAsync(seed.ADeleted.Id);

        Assert.True(getA.IsSuccess);
        Assert.Equal(seed.AActive.Id, getA.Value.Id);
        Assert.IsType<NotFoundFailure<Guid>>(getBFromA.Failure);
        Assert.IsType<NotFoundFailure<Guid>>(getDeletedA.Failure);

        DataResult<IReadOnlyList<Order>> listA = await repositoryA.ListAsync(new OrderSpecification());
        DataResult<IReadOnlyList<Order>> listB = await repositoryB.ListAsync(new OrderSpecification());
        Assert.True(listA.IsSuccess, listA.Failure?.ToString());
        Assert.Single(listA.Value);
        Assert.Equal(seed.TenantA, listA.Value[0].TenantId);
        Assert.True(listB.IsSuccess, listB.Failure?.ToString());
        Assert.Single(listB.Value);
        Assert.Equal(seed.TenantB, listB.Value[0].TenantId);

        DataResult<Order> firstA = await repositoryA.FirstOrDefaultAsync(
            new OrderSpecification(order => order.Name == "A-active"));
        DataResult<Order> firstBFromA = await repositoryA.FirstOrDefaultAsync(
            new OrderSpecification(order => order.Name == "B-active"));
        Assert.True(firstA.IsSuccess);
        Assert.Equal(seed.AActive.Id, firstA.Value.Id);
        Assert.IsType<ReadNotFoundFailure>(firstBFromA.Failure);

        var pageSpecification = new OrderProjectionSpecification(
            sorts:
            [
                new SortExpression<Order, string>(
                    order => order.Name,
                    SortDirection.Ascending),
            ]);
        DataResult<PageResult<OrderDto>> pageA = await repositoryA.PageAsync(
            pageSpecification,
            new PageRequest(1, 10));
        DataResult<PageResult<OrderDto>> pageB = await repositoryB.PageAsync(
            pageSpecification,
            new PageRequest(1, 10));
        Assert.True(pageA.IsSuccess, pageA.Failure?.ToString());
        Assert.True(pageB.IsSuccess, pageB.Failure?.ToString());
        Assert.Single(pageA.Value.Items);
        Assert.Single(pageB.Value.Items);
        Assert.All(pageA.Value.Items, item => Assert.Equal(seed.TenantA, item.TenantId));
        Assert.All(pageB.Value.Items, item => Assert.Equal(seed.TenantB, item.TenantId));

        DataResult<long> countA = await repositoryA.CountAsync(new OrderSpecification());
        DataResult<long> countB = await repositoryB.CountAsync(new OrderSpecification());
        Assert.Equal(1, countA.Value);
        Assert.Equal(1, countB.Value);

        DataResult<bool> anyA = await repositoryA.AnyAsync(
            new OrderSpecification(order => order.Name == "A-active"));
        DataResult<bool> anyBFromA = await repositoryA.AnyAsync(
            new OrderSpecification(order => order.Name == "B-active"));
        Assert.True(anyA.Value);
        Assert.False(anyBFromA.Value);

        Assert.Empty(contextA.ChangeTracker.Entries<Order>());
        Assert.Empty(contextB.ChangeTracker.Entries<Order>());
    }

    [Fact]
    public async Task ProjectionCannotLeakAnotherTenant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        var repository = CreateRepository(context, store, seed.TenantA);

        DataResult<OrderDto> projected = await repository.GetByIdAsync(
            seed.AActive.Id,
            new OrderProjection());
        DataResult<OrderDto> projectedOtherTenant = await repository.GetByIdAsync(
            seed.BActive.Id,
            new OrderProjection());
        DataResult<IReadOnlyList<OrderDto>> projectedList = await repository.ListAsync(
            new OrderProjectionSpecification());
        DataResult<OrderDto> projectedFirst = await repository.FirstOrDefaultAsync(
            new OrderProjectionSpecification(order => order.Name == "A-active"));
        DataResult<OrderDto> projectedFirstOther = await repository.FirstOrDefaultAsync(
            new OrderProjectionSpecification(order => order.Name == "B-active"));

        Assert.True(projected.IsSuccess);
        Assert.Equal(seed.TenantA, projected.Value.TenantId);
        Assert.IsType<NotFoundFailure<Guid>>(projectedOtherTenant.Failure);
        Assert.True(projectedList.IsSuccess, projectedList.Failure?.ToString());
        Assert.Single(projectedList.Value);
        Assert.All(projectedList.Value, item => Assert.Equal(seed.TenantA, item.TenantId));
        Assert.True(projectedFirst.IsSuccess);
        Assert.IsType<ReadNotFoundFailure>(projectedFirstOther.Failure);
    }

    [Fact]
    public async Task TenantResolutionFailureExecutesNoSql()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await SeedAsync(database, context, store);
        interceptor.Reset();

        var resolver = new ReadTenantResolver(Guid.NewGuid()) { Fail = true };
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        var repository = new EfReadRepository<TestDbContext, Order, Guid, Guid>(
            context,
            coordinator,
            order => order.Id);

        Assert.True((await repository.GetByIdAsync(Guid.NewGuid())).IsFailure);
        Assert.Empty(interceptor.Commands);

        Assert.True((await repository.ListAsync(new OrderSpecification())).IsFailure);
        Assert.Empty(interceptor.Commands);

        Assert.True((await repository.FirstOrDefaultAsync(new OrderSpecification())).IsFailure);
        Assert.Empty(interceptor.Commands);

        Assert.True((await repository.CountAsync(new OrderSpecification())).IsFailure);
        Assert.Empty(interceptor.Commands);

        Assert.True((await repository.AnyAsync(new OrderSpecification())).IsFailure);
        Assert.Empty(interceptor.Commands);

        var pageSpecification = new OrderProjectionSpecification(
            sorts:
            [
                new SortExpression<Order, string>(
                    order => order.Name,
                    SortDirection.Ascending),
            ]);
        Assert.True((await repository.PageAsync(pageSpecification, new PageRequest(1, 10))).IsFailure);
        Assert.Empty(interceptor.Commands);
    }

    [Fact]
    public async Task UnsupportedProjectionIsRejectedWithoutSql()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await SeedAsync(database, context, store);
        interceptor.Reset();

        var repository = CreateRepository(context, store, Guid.NewGuid());
        DataResult<IReadOnlyList<string>> result = await repository.ListAsync(
            new UnsupportedProjectionSpecification());

        Assert.True(result.IsFailure);
        Assert.IsType<UnsupportedExpressionFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);
    }

    [Fact]
    public async Task UnsupportedCriteriaIsRejectedWithoutSql()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await SeedAsync(database, context, store);
        interceptor.Reset();

        var repository = CreateRepository(context, store, Guid.NewGuid());
        var specification = new OrderSpecification(
            order => UnsupportedPredicate(order.Name));

        DataResult<IReadOnlyList<Order>> result = await repository.ListAsync(specification);

        Assert.True(result.IsFailure);
        Assert.IsType<UnsupportedExpressionFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);
    }

    [Fact]
    public async Task GeneratedSqlContainsTenantAndSoftDeletePredicates()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        SeedData seed = await SeedAsync(database, context, store);
        interceptor.Reset();

        var repository = CreateRepository(context, store, seed.TenantA);
        DataResult<IReadOnlyList<Order>> result = await repository.ListAsync(new OrderSpecification());

        Assert.True(result.IsSuccess, result.Failure?.ToString());
        Assert.Single(interceptor.Commands);
        string sql = interceptor.Commands[0];
        Assert.Contains("TenantId", sql, StringComparison.Ordinal);
        Assert.Contains("IsDeleted", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PageUsesStableOrderingAndDoesNotDuplicateRows()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenant = Guid.NewGuid();
        Order[] orders = Enumerable.Range(0, 5)
            .Select(_ => NewOrder(tenant, "same-name"))
            .ToArray();
        await TestSeeds.SaveOrdersAsync(context, store, tenant, orders);
        context.ChangeTracker.Clear();
        store.Clear();

        var repository = CreateRepository(context, store, tenant);
        var specification = new OrderProjectionSpecification(
            sorts:
            [
                new SortExpression<Order, string>(
                    order => order.Name,
                    SortDirection.Ascending),
            ]);

        DataResult<PageResult<OrderDto>> page1 = await repository.PageAsync(
            specification,
            new PageRequest(1, 2));
        DataResult<PageResult<OrderDto>> page2 = await repository.PageAsync(
            specification,
            new PageRequest(2, 2));
        DataResult<PageResult<OrderDto>> page3 = await repository.PageAsync(
            specification,
            new PageRequest(3, 2));

        Assert.True(page1.IsSuccess, page1.Failure?.ToString());
        Assert.True(page2.IsSuccess, page2.Failure?.ToString());
        Assert.True(page3.IsSuccess, page3.Failure?.ToString());
        Assert.Equal(5, page1.Value.TotalCount);
        Assert.Equal(2, page1.Value.Items.Count);
        Assert.Equal(2, page2.Value.Items.Count);
        Assert.Single(page3.Value.Items);

        Guid[] allIds = page1.Value.Items
            .Concat(page2.Value.Items)
            .Concat(page3.Value.Items)
            .Select(item => item.Id)
            .ToArray();

        Assert.Equal(5, allIds.Distinct().Count());

        DataResult<PageResult<OrderDto>> page1Again = await repository.PageAsync(
            specification,
            new PageRequest(1, 2));
        Assert.Equal(
            page1.Value.Items.Select(item => item.Id),
            page1Again.Value.Items.Select(item => item.Id));
    }

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
            new ReadTenantResolver(Guid.NewGuid()),
            store);

        Assert.Throws<ArgumentException>(
            () => new EfReadRepository<TestDbContext, Order, Guid, Guid>(
                context,
                coordinator,
                order => order.TenantId));
    }

    [Fact]
    public async Task PageSqlIncludesPrimaryKeyTieBreaker()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid tenant = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, tenant, NewOrder(tenant, "paged"));
        context.ChangeTracker.Clear();
        store.Clear();
        interceptor.Reset();

        var repository = CreateRepository(context, store, tenant);
        var specification = new OrderProjectionSpecification(
            sorts:
            [
                new SortExpression<Order, string>(
                    order => order.Name,
                    SortDirection.Ascending),
            ]);

        DataResult<PageResult<OrderDto>> result = await repository.PageAsync(
            specification,
            new PageRequest(1, 10));

        Assert.True(result.IsSuccess, result.Failure?.ToString());
        string pageSql = interceptor.Commands[^1];
        Assert.Contains("ORDER BY", pageSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Id", pageSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleSortsUseThenByAndDirection()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenant = Guid.NewGuid();
        var first = NewOrder(tenant, "b");
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var second = NewOrder(tenant, "a");
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var third = NewOrder(tenant, "b");
        third.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        await TestSeeds.SaveOrdersAsync(context, store, tenant, first, second, third);
        context.ChangeTracker.Clear();
        store.Clear();

        var repository = CreateRepository(context, store, tenant);
        var specification = new OrderSpecification(
            sorts:
            [
                new SortExpression<Order, string>(
                    order => order.Name,
                    SortDirection.Ascending),
                new SortExpression<Order, Guid>(
                    order => order.Id,
                    SortDirection.Descending),
            ]);

        DataResult<IReadOnlyList<Order>> result = await repository.ListAsync(specification);

        Assert.True(result.IsSuccess, result.Failure?.ToString());
        Assert.Equal(
            [second.Id, third.Id, first.Id],
            result.Value.Select(order => order.Id));
    }

    [Fact]
    public async Task PageWithoutStableOrderFailsWithoutSql()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        await SeedAsync(database, context, store);
        interceptor.Reset();

        var repository = CreateRepository(context, store, Guid.NewGuid());
        DataResult<PageResult<OrderDto>> result = await repository.PageAsync(
            new OrderProjectionSpecification(),
            new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.IsType<UnstableOrderingFailure>(result.Failure);
        Assert.Empty(interceptor.Commands);
    }

    [Fact]
    public async Task TenantScopeChangeAttemptFailsWithoutSql()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var interceptor = new CapturingCommandInterceptor();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.CreateWithInterceptor(
            database,
            store,
            interceptor,
            TestContexts.OrderTenantMapping());
        SeedData seed = await SeedAsync(database, context, store);
        Assert.True(store.TrySet(seed.TenantA, TenantOperation.Read));
        interceptor.Reset();

        var resolver = new ReadTenantResolver(seed.TenantB);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        var repository = new EfReadRepository<TestDbContext, Order, Guid, Guid>(
            context,
            coordinator,
            order => order.Id);

        DataResult<IReadOnlyList<Order>> result = await repository.ListAsync(new OrderSpecification());

        Assert.True(result.IsFailure);
        Assert.IsType<AuthorizationFailure>(result.Failure);
        Assert.Equal(seed.TenantA, store.TenantId);
        Assert.Empty(interceptor.Commands);
    }

    private static EfReadRepository<TestDbContext, Order, Guid, Guid> CreateRepository(
        TestDbContext context,
        TenantScopeStore store,
        Guid tenantId)
    {
        var resolver = new ReadTenantResolver(tenantId);
        var coordinator = new TenantScopeCoordinator<Order, Guid>(resolver, store);
        return new EfReadRepository<TestDbContext, Order, Guid, Guid>(
            context,
            coordinator,
            order => order.Id);
    }

    private static async Task<SeedData> SeedAsync(SqliteTestDatabase database)
    {
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        return await SeedAsync(database, context, store);
    }

    private static async Task<SeedData> SeedAsync(
        SqliteTestDatabase database,
        TestDbContext context,
        TenantScopeStore store)
    {
        await context.Database.EnsureCreatedAsync();

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        var aActive = NewOrder(tenantA, "A-active");
        var aDeleted = NewOrder(tenantA, "A-deleted", isDeleted: true);
        var bActive = NewOrder(tenantB, "B-active");
        var bDeleted = NewOrder(tenantB, "B-deleted", isDeleted: true);

        await TestSeeds.SaveOrdersAsync(context, store, tenantA, aActive, aDeleted);
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, bActive, bDeleted);
        context.ChangeTracker.Clear();
        store.Clear();

        return new SeedData(tenantA, tenantB, aActive, aDeleted, bActive, bDeleted);
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
            IsDeleted = isDeleted,
            DeletedAtUtc = isDeleted ? DateTimeOffset.UtcNow : null,
        };
    }

    private static bool UnsupportedPredicate(string value) => value.Length > 0;

    private sealed record SeedData(
        Guid TenantA,
        Guid TenantB,
        Order AActive,
        Order ADeleted,
        Order BActive,
        Order BDeleted);

    private sealed record OrderDto(
        Guid Id,
        Guid TenantId,
        string Name,
        bool IsDeleted);

    private sealed class OrderProjection : IProjection<Order, OrderDto>
    {
        public Expression<Func<Order, OrderDto>> Selector =>
            order => new OrderDto(order.Id, order.TenantId, order.Name, order.IsDeleted);
    }

    private sealed class OrderSpecification : ISpecification<Order>
    {
        public OrderSpecification(
            Expression<Func<Order, bool>>? criteria = null,
            IReadOnlyList<ISortExpression<Order>>? sorts = null)
        {
            Criteria = criteria;
            Sorts = sorts ?? [];
        }

        public Expression<Func<Order, bool>>? Criteria { get; }

        public IReadOnlyList<ISortExpression<Order>> Sorts { get; }
    }

    private sealed class OrderProjectionSpecification : IProjectionSpecification<Order, OrderDto>
    {
        public OrderProjectionSpecification(
            Expression<Func<Order, bool>>? criteria = null,
            IReadOnlyList<ISortExpression<Order>>? sorts = null)
        {
            Criteria = criteria;
            Sorts = sorts ?? [];
        }

        public Expression<Func<Order, bool>>? Criteria { get; }

        public IReadOnlyList<ISortExpression<Order>> Sorts { get; }

        public Expression<Func<Order, OrderDto>> Selector =>
            order => new OrderDto(order.Id, order.TenantId, order.Name, order.IsDeleted);
    }

    private sealed class UnsupportedProjectionSpecification
        : IProjectionSpecification<Order, string>
    {
        public Expression<Func<Order, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<Order>> Sorts => [];

        public Expression<Func<Order, string>> Selector =>
            order => UnsupportedProjectionHelper.Normalize(order.Name);
    }

    private static class UnsupportedProjectionHelper
    {
        public static string Normalize(string value) => value;
    }

    private sealed class ReadTenantResolver(Guid tenantId) : ITenantAccessResolver<Order, Guid>
    {
        public Guid TenantId { get; } = tenantId;

        public bool Fail { get; init; }

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
}
