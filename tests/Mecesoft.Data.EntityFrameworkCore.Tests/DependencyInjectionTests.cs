using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Authorization;
using Mecesoft.Data.EntityFrameworkCore.DependencyInjection;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task ContextStoreAndUnitOfWorkAreScopedAndNotPooled()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        TestDbContext firstContext = firstScope.ServiceProvider.GetRequiredService<TestDbContext>();
        TestDbContext sameContext = firstScope.ServiceProvider.GetRequiredService<TestDbContext>();
        TestDbContext secondContext = secondScope.ServiceProvider.GetRequiredService<TestDbContext>();
        IUnitOfWork firstUnitOfWork = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IUnitOfWork secondUnitOfWork = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        ITenantScopeStore firstStore = firstScope.ServiceProvider.GetRequiredService<ITenantScopeStore>();
        ITenantScopeStore secondStore = secondScope.ServiceProvider.GetRequiredService<ITenantScopeStore>();

        Assert.Same(firstContext, sameContext);
        Assert.NotSame(firstContext, secondContext);
        Assert.NotSame(firstUnitOfWork, secondUnitOfWork);
        Assert.NotSame(firstStore, secondStore);

        Assert.NotNull(firstContext.Model.FindEntityType(typeof(Order)));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.FullName?.Contains("DbContextPool", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ReadRepositoryResolvesAsScopedAndUsesTheRegisteredResolver()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);
        services.AddScoped<ITenantAccessResolver<Order, Guid>, DiReadTenantResolver>();
        services.AddMecesoftReadRepository<TestDbContext, Order, Guid, Guid>(order => order.Id);
        services.AddMecesoftWriteRepository<TestDbContext, Order, Guid, Guid>(order => order.Id);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        IReadRepository<Order, Guid> first = firstScope.ServiceProvider
            .GetRequiredService<IReadRepository<Order, Guid>>();
        IReadRepository<Order, Guid> same = firstScope.ServiceProvider
            .GetRequiredService<IReadRepository<Order, Guid>>();
        IReadRepository<Order, Guid> second = secondScope.ServiceProvider
            .GetRequiredService<IReadRepository<Order, Guid>>();
        IRepository<Order, Guid> writeFirst = firstScope.ServiceProvider
            .GetRequiredService<IRepository<Order, Guid>>();
        IRepository<Order, Guid> writeSecond = secondScope.ServiceProvider
            .GetRequiredService<IRepository<Order, Guid>>();

        Assert.Same(first, same);
        Assert.NotSame(first, second);
        Assert.NotSame(writeFirst, writeSecond);
    }

    [Fact]
    public void FoundationServicesUseScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite("Data Source=:memory:"));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);
        services.AddMecesoftReadRepository<TestDbContext, Order, Guid, Guid>(order => order.Id);
        services.AddMecesoftWriteRepository<TestDbContext, Order, Guid, Guid>(order => order.Id);

        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(TestDbContext)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(
                descriptor => descriptor.ServiceType == typeof(IUnitOfWork)
                    && descriptor.ServiceKey is null).Lifetime);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IUnitOfWork)
                && descriptor.ServiceKey is not null
                && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(ITenantScopeStore)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(TenantScopeCoordinator<,>)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(IReadRepository<Order, Guid>)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(descriptor => descriptor.ServiceType == typeof(IRepository<Order, Guid>)).Lifetime);
    }

    [Fact]
    public void GlobalWriteAuthorizationRegistersAuthorizerAndCoordinator()
    {
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite("Data Source=:memory:"));
        services.AddMecesoftGlobalWriteAuthorization<AllowAllGlobalWriteAuthorizer>();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGlobalWriteAuthorizer>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GlobalWriteCoordinator>());
    }

    [Fact]
    public void TenantMappingsAreRegisteredAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);

        ServiceDescriptor[] mappings = services
            .Where(descriptor => descriptor.ServiceType == typeof(ITenantEntityMapping))
            .ToArray();
        Assert.Equal(2, mappings.Length);
        Assert.All(
            mappings,
            descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
    }

    [Fact]
    public void SoftDeleteMappingsAreRegisteredAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddSoftDeleteEntity<Order>();

        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping));
        Assert.Equal(
            ServiceLifetime.Singleton,
            services.Single(descriptor => descriptor.ServiceType == typeof(Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping)).Lifetime);
    }

    [Fact]
    public async Task SoftDeleteRepositoryResolvesAsScopedAndUsesTheRegisteredAuthorizer()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);
        services.AddScoped<ITenantAccessResolver<Order, Guid>, DiReadTenantResolver>();
        services.AddScoped<ISoftDeleteAuthorizer<Order, Guid, Guid>, DiSoftDeleteAuthorizer>();
        services.AddMecesoftSoftDeleteRepository<TestDbContext, Order, Guid, Guid>(
            order => order.Id);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope firstScope = provider.CreateAsyncScope();
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();

        ISoftDeleteRepository<Order, Guid> first = firstScope.ServiceProvider
            .GetRequiredService<ISoftDeleteRepository<Order, Guid>>();
        ISoftDeleteRepository<Order, Guid> same = firstScope.ServiceProvider
            .GetRequiredService<ISoftDeleteRepository<Order, Guid>>();
        ISoftDeleteRepository<Order, Guid> second = secondScope.ServiceProvider
            .GetRequiredService<ISoftDeleteRepository<Order, Guid>>();

        Assert.Same(first, same);
        Assert.NotSame(first, second);
        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(
                descriptor => descriptor.ServiceType == typeof(ISoftDeleteRepository<Order, Guid>)).Lifetime);
    }

    [Fact]
    public async Task MultiContextRegistrationsUseContextSpecificUnitOfWork()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<SecondTestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);
        services.AddScoped<ITenantAccessResolver<Order, Guid>, DiReadTenantResolver>();
        services.AddMecesoftWriteRepository<TestDbContext, Order, Guid, Guid>(
            order => order.Id);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        IRepository<Order, Guid> repository = scope.ServiceProvider
            .GetRequiredService<IRepository<Order, Guid>>();
        IUnitOfWork expected = scope.ServiceProvider
            .GetRequiredKeyedService<IUnitOfWork>(typeof(TestDbContext));
        IUnitOfWork other = scope.ServiceProvider
            .GetRequiredKeyedService<IUnitOfWork>(typeof(SecondTestDbContext));

        Assert.Same(expected, repository.UnitOfWork);
        Assert.NotSame(other, repository.UnitOfWork);
        Assert.NotSame(expected, other);
        Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

        TestDbContext context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.True((await repository.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        Assert.NotNull(context.Database.CurrentTransaction);
        Assert.True((await repository.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [Fact]
    public async Task SoftDeleteRepositoryFailsClosedWithoutAnAuthorizer()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddMecesoftDbContext<TestDbContext>(
            options => options.UseSqlite(database.Connection));
        services.AddTenantEntity<Order, Guid>(order => order.TenantId);
        services.AddTenantEntity<OrderLine, Guid>(line => line.TenantId);
        services.AddScoped<ITenantAccessResolver<Order, Guid>, DiReadTenantResolver>();
        services.AddMecesoftSoftDeleteRepository<TestDbContext, Order, Guid, Guid>(
            order => order.Id);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<ISoftDeleteRepository<Order, Guid>>());
    }
}

public sealed class DiReadTenantResolver : ITenantAccessResolver<Order, Guid>
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public Expression<Func<Order, Guid>> TenantIdSelector => order => order.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
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

public sealed class DiSoftDeleteAuthorizer : ISoftDeleteAuthorizer<Order, Guid, Guid>
{
    public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
        SoftDeleteAuthorizationContext<Order, Guid, Guid> context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(DataResult.Succeeded(context.Operation));
    }
}
