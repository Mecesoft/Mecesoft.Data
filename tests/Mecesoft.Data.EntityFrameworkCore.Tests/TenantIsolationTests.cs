using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class TenantIsolationTests
{
    [Fact]
    public async Task TenantAAndBTenantFiltersAreIsolated()
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
        await TestSeeds.SaveOrdersAsync(context, store, tenantA, NewOrder(tenantA, "A-1"));
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, NewOrder(tenantB, "B-1"));
        context.ChangeTracker.Clear();
        store.Clear();

        Assert.True(store.TrySet(tenantA, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantA);
        List<Order> tenantAOrders = await context.Orders
            .AsNoTracking()
            .ToListAsync();
        Assert.Single(tenantAOrders);
        Assert.Equal(tenantA, tenantAOrders[0].TenantId);

        store.Clear();
        Assert.True(store.TrySet(tenantB, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantB);
        List<Order> tenantBOrders = await context.Orders
            .AsNoTracking()
            .ToListAsync();
        Assert.Single(tenantBOrders);
        Assert.Equal(tenantB, tenantBOrders[0].TenantId);
    }

    [Fact]
    public async Task IgnoringSoftDeleteKeepsTheTenantFilter()
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
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantA,
            NewOrder(tenantA, "A-active"),
            NewOrder(tenantA, "A-deleted", isDeleted: true));
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantB,
            NewOrder(tenantB, "B-active"),
            NewOrder(tenantB, "B-deleted", isDeleted: true));
        context.ChangeTracker.Clear();
        store.Clear();

        Assert.True(store.TrySet(tenantA, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantA);

        List<Order> active = await context.Orders
            .AsNoTracking()
            .ToListAsync();
        Assert.Single(active);
        Assert.Equal("A-active", active[0].Name);

        List<Order> includingDeleted;
        using (SoftDeleteReadAuthorization.Begin(
            tenantA,
            SoftDeleteOperation.SoftDelete))
        {
            includingDeleted = await context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Assert.Equal(2, includingDeleted.Count);
        Assert.All(includingDeleted, order => Assert.Equal(tenantA, order.TenantId));
        Assert.DoesNotContain(includingDeleted, order => order.TenantId == tenantB);
    }

    [Fact]
    public async Task ModelContainsNamedTenantAndSoftDeleteFilters()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());

        var orderType = context.Model.FindEntityType(typeof(Order));

        Assert.NotNull(orderType);
        Assert.NotNull(orderType.FindDeclaredQueryFilter(MecesoftQueryFilterKeys.Tenant));
        Assert.NotNull(orderType.FindDeclaredQueryFilter(MecesoftQueryFilterKeys.SoftDelete));
    }

    [Fact]
    public void MissingTenantMappingFailsModelCreation()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<UnmappedDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var context = new UnmappedDbContext(
            options,
            store,
            Array.Empty<TenantEntityMapping<Order, Guid>>(),
            Array.Empty<Mecesoft.Data.EntityFrameworkCore.SoftDelete.SoftDeleteEntityMapping<Order>>());

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public void NestedTenantSelectorIsRejectedByMapping()
    {
        Assert.Throws<ArgumentException>(
            () => new TenantEntityMapping<NestedTenantEntity, Guid>(
                entity => entity.Tenant.Id));
    }

    [Fact]
    public void InvalidTenantSelectorIsRejectedAtMappingConstruction()
    {
        Assert.Throws<ArgumentException>(
            () => new TenantEntityMapping<Order, Guid>(_ => Guid.Empty));
    }

    [Fact]
    public void DuplicateTenantMappingsFailModelCreation()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<DuplicateTenantDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var first = new TenantEntityMapping<Order, Guid>(order => order.TenantId);
        var second = new TenantEntityMapping<Order, Guid>(order => order.TenantId);

        using var context = new DuplicateTenantDbContext(
            options,
            store,
            [first, second],
            Array.Empty<Mecesoft.Data.EntityFrameworkCore.SoftDelete.SoftDeleteEntityMapping<Order>>());

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public async Task TenantFilterUsesTheCurrentContextInstance()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var seedStore = new TenantScopeStore();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using (TestDbContext seedContext = TestContexts.Create(
            database,
            seedStore,
            TestContexts.OrderTenantMapping()))
        {
            await seedContext.Database.EnsureCreatedAsync();
            await TestSeeds.SaveOrdersAsync(seedContext, seedStore, tenantA, NewOrder(tenantA, "A"));
            await TestSeeds.SaveOrdersAsync(seedContext, seedStore, tenantB, NewOrder(tenantB, "B"));
        }

        var storeA = new TenantScopeStore();
        var storeB = new TenantScopeStore();
        Assert.True(storeA.TrySet(tenantA, TenantOperation.Read));
        Assert.True(storeB.TrySet(tenantB, TenantOperation.Read));

        await using TestDbContext contextA = TestContexts.Create(
            database,
            storeA,
            TestContexts.OrderTenantMapping());
        await using TestDbContext contextB = TestContexts.Create(
            database,
            storeB,
            TestContexts.OrderTenantMapping());
        contextA.AuthorizeReadForTesting(tenantA);
        contextB.AuthorizeReadForTesting(tenantB);

        Order orderA = Assert.Single(await contextA.Orders.AsNoTracking().ToListAsync());
        Order orderB = Assert.Single(await contextB.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(tenantA, orderA.TenantId);
        Assert.Equal(tenantB, orderB.TenantId);
        Assert.NotEqual(orderA.TenantId, orderB.TenantId);
    }

    [Fact]
    public async Task SharedOptionsUseTheCurrentContextTenant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        var seedStore = new TenantScopeStore();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        await using (var seedContext = new TestDbContext(
            options,
            seedStore,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []))
        {
            await seedContext.Database.EnsureCreatedAsync();
            await TestSeeds.SaveOrdersAsync(seedContext, seedStore, tenantA, NewOrder(tenantA, "A"));
            await TestSeeds.SaveOrdersAsync(seedContext, seedStore, tenantB, NewOrder(tenantB, "B"));
        }

        var storeA = new TenantScopeStore();
        var storeB = new TenantScopeStore();
        Assert.True(storeA.TrySet(tenantA, TenantOperation.Read));
        Assert.True(storeB.TrySet(tenantB, TenantOperation.Read));

        await using var contextA = new TestDbContext(
            options,
            storeA,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await using var contextB = new TestDbContext(
            options,
            storeB,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        contextA.AuthorizeReadForTesting(tenantA);
        contextB.AuthorizeReadForTesting(tenantB);

        Order orderA = Assert.Single(await contextA.Orders.AsNoTracking().ToListAsync());
        Order orderB = Assert.Single(await contextB.Orders.AsNoTracking().ToListAsync());

        Assert.Equal(tenantA, orderA.TenantId);
        Assert.Equal(tenantB, orderB.TenantId);
    }

    [Fact]
    public async Task QueryWithoutTenantFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();
        Guid hiddenTenant = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(context, store, hiddenTenant, NewOrder(hiddenTenant, "hidden"));
        context.ChangeTracker.Clear();
        store.Clear();

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StringTenantIdsAreIsolated()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var mapping = new Mecesoft.Data.EntityFrameworkCore.Tenancy.TenantEntityMapping<StringTenantEntity, string>(
            entity => entity.TenantId);
        await using var context = new StringTenantDbContext(
            database.Options<StringTenantDbContext>(),
            store,
            [mapping],
            []);
        await context.Database.EnsureCreatedAsync();

        await TestSeeds.SaveStringItemsAsync(
            context,
            store,
            "tenant-a",
            new StringTenantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = "tenant-a",
                Name = "A",
            });
        await TestSeeds.SaveStringItemsAsync(
            context,
            store,
            "tenant-b",
            new StringTenantEntity
            {
                Id = Guid.NewGuid(),
                TenantId = "tenant-b",
                Name = "B",
            });
        context.ChangeTracker.Clear();
        store.Clear();

        Assert.True(store.TrySet("tenant-a", TenantOperation.Read));
        context.AuthorizeReadForTesting("tenant-a");
        StringTenantEntity itemA = Assert.Single(
            await context.Items.AsNoTracking().ToListAsync());
        Assert.Equal("tenant-a", itemA.TenantId);

        store.Clear();
        Assert.True(store.TrySet("tenant-b", TenantOperation.Read));
        context.AuthorizeReadForTesting("tenant-b");
        StringTenantEntity itemB = Assert.Single(
            await context.Items.AsNoTracking().ToListAsync());
        Assert.Equal("tenant-b", itemB.TenantId);
    }

    [Fact]
    public void StringTenantMappingWithoutRuntimeVerifierFailsModelCreation()
    {
        var store = new TenantScopeStore();
        var options = new DbContextOptionsBuilder<UnverifiedStringDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var mapping = new Mecesoft.Data.EntityFrameworkCore.Tenancy.TenantEntityMapping<StringTenantEntity, string>(
            entity => entity.TenantId);
        using var context = new UnverifiedStringDbContext(
            options,
            store,
            [mapping],
            []);

        Assert.Throws<TenantConfigurationException>(() => _ = context.Model);
    }

    [Fact]
    public async Task CustomMappingCannotWidenDirectDbSetIncludeOrAggregates()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            new DelegatingOrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Order orderA = NewOrder(tenantA, "A");
        orderA.Lines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderA.Id,
            TenantId = tenantA,
        });
        Order orderB = NewOrder(tenantB, "B");
        orderB.Lines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderB.Id,
            TenantId = tenantB,
        });

        await TestSeeds.SaveOrdersAsync(context, store, tenantA, orderA);
        await TestSeeds.SaveOrdersAsync(context, store, tenantB, orderB);
        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Read));
        context.AuthorizeReadForTesting(tenantA);

        List<Order> direct = await context.Orders
            .AsNoTracking()
            .Include(order => order.Lines)
            .ToListAsync();
        Order directA = Assert.Single(direct);
        Assert.Equal(tenantA, directA.TenantId);
        Assert.Single(directA.Lines);
        Assert.Equal(tenantA, directA.Lines[0].TenantId);

        var projection = await context.Orders
            .AsNoTracking()
            .Select(order => new { order.Id, order.TenantId })
            .ToListAsync();
        Assert.Single(projection);
        Assert.Equal(tenantA, projection[0].TenantId);

        Assert.Equal(1, await context.Orders.CountAsync());
        Assert.True(await context.Orders.AnyAsync(order => order.Id == orderA.Id));
        Assert.False(await context.Orders.AnyAsync(order => order.Id == orderB.Id));

        List<Order> page = await context.Orders
            .AsNoTracking()
            .OrderBy(order => order.Name)
            .Skip(0)
            .Take(10)
            .ToListAsync();
        Assert.Single(page);
        Assert.Equal(tenantA, page[0].TenantId);
    }

    private sealed class DelegatingOrderTenantMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public Type EntityType => typeof(Order);

        public Type TenantIdType => typeof(Guid);

        public string TenantPropertyName => nameof(Order.TenantId);

        public object? GetTenantId(object entity)
            => _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed record NestedTenantEntity(TenantInfo Tenant) : ITenantEntity<Guid>
    {
        public Guid TenantId => Tenant.Id;
    }

    private sealed record TenantInfo(Guid Id);

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
}
