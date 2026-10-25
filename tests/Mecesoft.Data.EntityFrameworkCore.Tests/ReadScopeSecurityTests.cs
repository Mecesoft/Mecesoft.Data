using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class ReadScopeSecurityTests
{
    [Fact]
    public async Task TrySetReadAloneCannotAuthorizeDirectDbSet()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "visible"));
        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet(tenantId, TenantOperation.Read));

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.AsNoTracking().ToListAsync());
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.AnyAsync());
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.Select(order => order.Name).ToListAsync());
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.OrderBy(order => order.Name).Skip(0).Take(1).ToListAsync());
    }

    [Fact]
    public async Task TrySetReadAloneCannotAuthorizeRepositoryRead()
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
        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantA),
            store);
        var repository = new EfReadRepository<TestDbContext, Order, Guid, Guid>(
            context,
            coordinator,
            order => order.Id);

        Assert.True(store.TrySet(tenantB, TenantOperation.Read));
        DataResult<IReadOnlyList<Order>> result =
            await repository.ListAsync(new AllOrdersSpecification());

        Assert.True(result.IsFailure);
        Assert.IsType<AuthorizationFailure>(result.Failure);
    }

    [Fact]
    public async Task AuthorizedDirectReadThroughCoordinatorSucceeds()
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
            NewOrder(tenantA, "A"));
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantB,
            NewOrder(tenantB, "B"));
        context.ChangeTracker.Clear();
        store.Clear();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantA),
            store);

        DataResult<int> result = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders
                .AsNoTracking()
                .CountAsync(cancellationToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task ClearAfterAuthorizedReadFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "A"));
        context.ChangeTracker.Clear();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<int> first = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders.CountAsync(cancellationToken));
        Assert.True(first.IsSuccess);

        store.Clear();

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.CountAsync());
    }

    [Fact]
    public async Task ScopeSwitchOnSameContextFailsClosed()
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

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantA),
            store);
        DataResult<int> first = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders.CountAsync(cancellationToken));
        Assert.True(first.IsSuccess);

        store.Clear();
        Assert.True(store.TrySet(tenantB, TenantOperation.Read));

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DeferredQueryAndAsyncEnumerationUseTheAuthorizedScope()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "A"));
        context.ChangeTracker.Clear();
        store.Clear();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        IQueryable<Order> deferred = context.Orders.AsNoTracking();
        var result = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await deferred.ToListAsync(cancellationToken));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);

        store.Clear();
        Assert.True(store.TrySet(Guid.NewGuid(), TenantOperation.Read));
        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => deferred.ToListAsync());
    }

    [Fact]
    public async Task CrossContextReadGrantCannotBeReused()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        DbContextOptions<TestDbContext> options = database.Options<TestDbContext>();
        var store = new TenantScopeStore();
        await using var contextA = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);
        await contextA.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            contextA,
            store,
            tenantId,
            NewOrder(tenantId, "A"));
        contextA.ChangeTracker.Clear();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<int> first = await coordinator.ExecuteAsync(
            contextA,
            TenantOperation.Read,
            async cancellationToken => await contextA.Orders.CountAsync(cancellationToken));
        Assert.True(first.IsSuccess);

        await using var contextB = new TestDbContext(
            options,
            store,
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => contextB.Orders.CountAsync());
    }

    [Fact]
    public async Task ProjectionCountAnyAndPaginationRequireAuthorizedScope()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        Guid tenantId = Guid.NewGuid();
        await TestSeeds.SaveOrdersAsync(
            context,
            store,
            tenantId,
            NewOrder(tenantId, "A"),
            NewOrder(tenantId, "B"));
        context.ChangeTracker.Clear();
        store.Clear();

        var coordinator = new TenantScopeCoordinator<Order, Guid>(
            new FixedOrderResolver(tenantId),
            store);
        DataResult<int> projected = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders
                .AsNoTracking()
                .Select(order => order.Name)
                .CountAsync(cancellationToken));
        Assert.True(projected.IsSuccess);
        Assert.Equal(2, projected.Value);

        DataResult<bool> any = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders
                .AnyAsync(cancellationToken));
        Assert.True(any.IsSuccess);
        Assert.True(any.Value);

        DataResult<List<Order>> page = await coordinator.ExecuteAsync(
            context,
            TenantOperation.Read,
            async cancellationToken => await context.Orders
                .AsNoTracking()
                .OrderBy(order => order.Name)
                .Skip(0)
                .Take(1)
                .ToListAsync(cancellationToken));
        Assert.True(page.IsSuccess);
        Assert.Single(page.Value);
    }

    [Fact]
    public async Task UseModelWithoutNamedFiltersFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        using var plain = new PlainNoFiltersDbContext(
            new DbContextOptionsBuilder<PlainNoFiltersDbContext>()
                .UseSqlite(database.Connection)
                .Options);
        var model = plain.Model;

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(database.Connection)
            .UseModel(model)
            .Options;

        Assert.Throws<TenantConfigurationException>(
            () => new TestDbContext(
                options,
                new TenantScopeStore(),
                [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
                []));
    }

    [Fact]
    public async Task UseModelWithoutSoftDeleteFilterFailsClosed()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        using var plain = new PlainTenantOnlyDbContext(
            new DbContextOptionsBuilder<PlainTenantOnlyDbContext>()
                .UseSqlite(database.Connection)
                .Options);
        var model = plain.Model;

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(database.Connection)
            .UseModel(model)
            .Options;

        Assert.Throws<TenantConfigurationException>(
            () => new TestDbContext(
                options,
                new TenantScopeStore(),
                [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
                []));
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

    private sealed class AllOrdersSpecification : ISpecification<Order>
    {
        public Expression<Func<Order, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<Order>> Sorts => [];
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

    private abstract class PlainTestDbContextBase : DbContext
    {
        protected PlainTestDbContextBase(DbContextOptions options)
            : base(options)
        {
        }

        public DbSet<Order> Orders => Set<Order>();

        public DbSet<OrderLine> OrderLines => Set<OrderLine>();

        public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Ignore<OrderNote>();

            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
                entity.Property(item => item.Version)
                    .IsConcurrencyToken()
                    .ValueGeneratedNever();
                entity.Ignore(order => order.Notes);
            });

            modelBuilder.Entity<OrderLine>(entity =>
            {
                entity.HasKey(line => line.Id);
                entity.Property(line => line.TenantId).IsRequired();
                entity.HasOne(line => line.Order)
                    .WithMany(order => order.Lines)
                    .HasForeignKey(line => line.OrderId);
            });

            modelBuilder.Entity<AuditEntry>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Message).IsRequired();
            });
        }
    }

    private sealed class PlainNoFiltersDbContext : PlainTestDbContextBase
    {
        public PlainNoFiltersDbContext(DbContextOptions<PlainNoFiltersDbContext> options)
            : base(options)
        {
        }
    }

    private sealed class PlainTenantOnlyDbContext : PlainTestDbContextBase
    {
        public PlainTenantOnlyDbContext(DbContextOptions<PlainTenantOnlyDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>()
                .HasQueryFilter("Tenant", order => order.TenantId == Guid.Empty);
            modelBuilder.Entity<OrderLine>()
                .HasQueryFilter("Tenant", line => line.TenantId == Guid.Empty);
        }
    }
}
