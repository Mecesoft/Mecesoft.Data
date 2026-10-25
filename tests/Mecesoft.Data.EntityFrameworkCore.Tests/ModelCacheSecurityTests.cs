using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class ModelCacheSecurityTests
{
    [Fact]
    public async Task SameMappingTypeSharesModelButSaveChangeRejectsChangedSelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();

        var firstStore = new TenantScopeStore();
        await using var first = CreateContext(
            options,
            firstStore,
            new MutableOrderMapping
            {
                UseIdSelector = false,
                UseInvalidFilter = false,
            });
        await first.Database.EnsureCreatedAsync();
        _ = first.Model;

        var secondStore = new TenantScopeStore();
        await using var second = CreateContext(
            options,
            secondStore,
            new MutableOrderMapping
            {
                UseIdSelector = true,
                UseInvalidFilter = false,
            });

        Assert.Same(first.Model, second.Model);

        secondStore.Clear();
        Assert.True(secondStore.TrySet(tenantA, TenantOperation.Create));
        secondStore.AuthorizeWriteForTesting(tenantA, TenantOperation.Create);
        second.AuthorizeReadForTesting(tenantA);
        second.Orders.Add(new Order
        {
            Id = tenantA,
            TenantId = tenantB,
            Name = "forged",
            Version = 1,
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => second.SaveChangesAsync());
        Assert.IsType<TenantConfigurationFailure>(exception.Failure);

        second.ChangeTracker.Clear();
        Assert.Empty(
            await second.Orders
                .IgnoreQueryFilters()
                .ToListAsync());
    }

    [Fact]
    public async Task SameMappingTypeSharesModelButUpdateRejectsChangedSelector()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();

        var firstStore = new TenantScopeStore();
        await using var first = CreateContext(
            options,
            firstStore,
            new MutableOrderMapping
            {
                UseIdSelector = false,
                UseInvalidFilter = false,
            });
        await first.Database.EnsureCreatedAsync();
        Order victim = NewOrder(tenantB, "victim");
        await TestSeeds.SaveOrdersAsync(first, firstStore, tenantB, victim);

        var secondStore = new TenantScopeStore();
        await using var second = CreateContext(
            options,
            secondStore,
            new MutableOrderMapping
            {
                UseIdSelector = true,
                UseInvalidFilter = false,
            });
        Assert.Same(first.Model, second.Model);

        secondStore.Clear();
        Assert.True(secondStore.TrySet(tenantA, TenantOperation.Update));
        secondStore.AuthorizeWriteForTesting(tenantA, TenantOperation.Update);
        var fabricated = new Order
        {
            Id = tenantA,
            TenantId = tenantB,
            Name = "hijacked",
            Version = 1,
        };
        second.Attach(fabricated);
        second.Entry(fabricated).State = EntityState.Modified;

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => second.SaveChangesAsync());
        Assert.IsType<TenantConfigurationFailure>(exception.Failure);

        second.ChangeTracker.Clear();
        firstStore.Clear();
        Assert.True(firstStore.TrySet(tenantB, TenantOperation.Read));
        first.AuthorizeReadForTesting(tenantB);
        Order persisted = Assert.Single(await first.Orders.AsNoTracking().ToListAsync());
        Assert.Equal("victim", persisted.Name);
        Assert.Equal(tenantB, persisted.TenantId);
    }

    [Fact]
    public async Task DifferentMappingTypesUseDifferentModels()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();

        using var first = CreateContext(
            options,
            new TenantScopeStore(),
            new DelegatingOrderMapping());
        _ = first.Model;

        using var second = CreateContext(
            options,
            new TenantScopeStore(),
            TestContexts.OrderTenantMapping());
        _ = second.Model;

        Assert.NotSame(first.Model, second.Model);
    }

    [Fact]
    public async Task MaliciousTenantPropertyIsRejectedBeforeCacheLookup()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();

        using var safe = CreateContext(
            options,
            new TenantScopeStore(),
            TestContexts.OrderTenantMapping());
        _ = safe.Model;

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => CreateContext(
                    options,
                    new TenantScopeStore(),
                    new WrongPropertyOrderMapping()));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongTenantIdTypeIsRejectedBeforeCacheLookup()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();

        using var safe = CreateContext(
            options,
            new TenantScopeStore(),
            TestContexts.OrderTenantMapping());
        _ = safe.Model;

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => CreateContext(
                    options,
                    new TenantScopeStore(),
                    new WrongTenantIdTypeOrderMapping()));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CacheHitStartupValidationRejectsChangedTenantFilter()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();

        using var first = CreateContext(
            options,
            new TenantScopeStore(),
            new MutableOrderMapping
            {
                UseIdSelector = false,
                UseInvalidFilter = false,
            });
        _ = first.Model;

        using var second = CreateContext(
            options,
            new TenantScopeStore(),
            new MutableOrderMapping
            {
                UseIdSelector = false,
                UseInvalidFilter = true,
            });

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => second.ValidateTenantSecurity());

        Assert.Contains(
            "predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentSoftDeleteMappingSetsUseDifferentModels()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        Guid tenantId = Guid.NewGuid();

        var defaultStore = new TenantScopeStore();
        await using var defaultContext = CreateContext(
            options,
            defaultStore,
            TestContexts.OrderTenantMapping(),
            new SoftDeleteEntityMapping<Order>());
        await defaultContext.Database.EnsureCreatedAsync();
        await TestSeeds.SaveOrdersAsync(
            defaultContext,
            defaultStore,
            tenantId,
            NewOrder(tenantId, "deleted", isDeleted: true));

        using var permissiveContext = CreateContext(
            options,
            new TenantScopeStore(),
            TestContexts.OrderTenantMapping(),
            new SoftDeleteEntityMapping<Order>(_ => true));

        Assert.NotSame(defaultContext.Model, permissiveContext.Model);

        defaultStore.Clear();
        Assert.True(defaultStore.TrySet(tenantId, TenantOperation.Read));
        defaultContext.AuthorizeReadForTesting(tenantId);
        Assert.Empty(await defaultContext.Orders.AsNoTracking().ToListAsync());

        var permissiveStore = new TenantScopeStore();
        Assert.True(permissiveStore.TrySet(tenantId, TenantOperation.Read));
        await using var scopedPermissiveContext = CreateContext(
            options,
            permissiveStore,
            TestContexts.OrderTenantMapping(),
            new SoftDeleteEntityMapping<Order>(_ => true));
        scopedPermissiveContext.AuthorizeReadForTesting(tenantId);
        Assert.Single(await scopedPermissiveContext.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SameMappingSetSharesModelAndKeepsTenantIsolation()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();

        var seedStore = new TenantScopeStore();
        await using var seedContext = CreateContext(
            options,
            seedStore,
            TestContexts.OrderTenantMapping());
        await seedContext.Database.EnsureCreatedAsync();
        await TestSeeds.SaveOrdersAsync(
            seedContext,
            seedStore,
            tenantA,
            NewOrder(tenantA, "A"));
        await TestSeeds.SaveOrdersAsync(
            seedContext,
            seedStore,
            tenantB,
            NewOrder(tenantB, "B"));

        var storeA = new TenantScopeStore();
        var storeB = new TenantScopeStore();
        Assert.True(storeA.TrySet(tenantA, TenantOperation.Read));
        Assert.True(storeB.TrySet(tenantB, TenantOperation.Read));

        await using var contextA = CreateContext(
            options,
            storeA,
            TestContexts.OrderTenantMapping());
        await using var contextB = CreateContext(
            options,
            storeB,
            TestContexts.OrderTenantMapping());
        contextA.AuthorizeReadForTesting(tenantA);
        contextB.AuthorizeReadForTesting(tenantB);

        Assert.Same(contextA.Model, contextB.Model);
        Order orderA = Assert.Single(await contextA.Orders.AsNoTracking().ToListAsync());
        Order orderB = Assert.Single(await contextB.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(tenantA, orderA.TenantId);
        Assert.Equal(tenantB, orderB.TenantId);
    }

    private static TestDbContext CreateContext(
        DbContextOptions<TestDbContext> options,
        TenantScopeStore store,
        ITenantEntityMapping orderMapping,
        params ISoftDeleteEntityMapping[] softDeleteMappings)
    {
        return new TestDbContext(
            options,
            store,
            [orderMapping, TestContexts.OrderLineTenantMapping()],
            softDeleteMappings);
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

    private sealed class DelegatingOrderMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public Type EntityType => typeof(Order);

        public Type TenantIdType => typeof(Guid);

        public string TenantPropertyName => nameof(Order.TenantId);

        public object? GetTenantId(object entity) => _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class WrongPropertyOrderMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public Type EntityType => typeof(Order);

        public Type TenantIdType => typeof(Guid);

        public string TenantPropertyName => nameof(Order.Id);

        public object? GetTenantId(object entity) => ((Order)entity).Id;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class WrongTenantIdTypeOrderMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public Type EntityType => typeof(Order);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(Order.TenantId);

        public object? GetTenantId(object entity) => _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class MutableOrderMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public bool UseIdSelector { get; init; }

        public bool UseInvalidFilter { get; init; }

        public Type EntityType => typeof(Order);

        public Type TenantIdType => typeof(Guid);

        public string TenantPropertyName => nameof(Order.TenantId);

        public object? GetTenantId(object entity)
            => UseIdSelector
                ? ((Order)entity).Id
                : _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => UseInvalidFilter
                ? Expression.Lambda(
                    Expression.Constant(true),
                    Expression.Parameter(typeof(Order), "entity"))
                : _inner.BuildFilter(context);
    }
}
