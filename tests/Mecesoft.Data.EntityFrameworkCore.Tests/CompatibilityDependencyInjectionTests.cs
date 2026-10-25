using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class CompatibilityDependencyInjectionTests
{
    [Fact]
    public void AddMecesoftDataRegistersRepositoryAndUnitOfWork()
    {
        var services = new ServiceCollection();
        services.AddMecesoftData<LegacyDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddMecesoftRepository<LegacyDbContext, LegacyProduct>();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRepository<LegacyProduct>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<LegacyDbContext>(),
            scope.ServiceProvider.GetRequiredService<LegacyDbContext>());
    }

    [Fact]
    public void AddMecesoftSoftDeleteServiceRegistersScopedService()
    {
        var services = new ServiceCollection();
        services.AddMecesoftData<LegacyDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddMecesoftSoftDeleteService<LegacyDbContext, LegacySoftEntity, Guid>(
            entity => entity.Id);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        Assert.NotNull(first.ServiceProvider.GetRequiredService<ISoftDeleteService<LegacySoftEntity, Guid>>());
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<ISoftDeleteService<LegacySoftEntity, Guid>>(),
            second.ServiceProvider.GetRequiredService<ISoftDeleteService<LegacySoftEntity, Guid>>());
    }

    [Fact]
    public void AddMecesoftDataRejectsMecesoftDbContext()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddMecesoftData<TestDbContext>(options => options.UseSqlite("Data Source=:memory:")));
    }

    [Fact]
    public async Task AddMecesoftTenantDataResolvesOneDatabasePerTenant()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "mecesoft-data-tenant-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string tenantA = "Data Source=" + Path.Combine(directory, "tenant-a.db");
        string tenantB = "Data Source=" + Path.Combine(directory, "tenant-b.db");

        try
        {
            var services = new ServiceCollection();
            services.AddMecesoftTenantData<LegacyDbContext, TestTenantConnectionStringResolver>(
                (options, connectionString) => options.UseSqlite(connectionString));
            services.AddMecesoftRepository<LegacyDbContext, LegacyProduct>();

            await using ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                TestTenantConnectionStringResolver.ConnectionString.Value = tenantA;
                LegacyDbContext context = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
                Assert.Equal(tenantA, context.Database.GetConnectionString());
                await context.Database.EnsureCreatedAsync();
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<LegacyProduct>>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await repository.AddAsync(new LegacyProduct
                {
                    Id = Guid.NewGuid(),
                    Name = "Tenant A",
                    Price = 1m,
                });
                await unitOfWork.SaveChangesAsync();
            }

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                TestTenantConnectionStringResolver.ConnectionString.Value = tenantB;
                LegacyDbContext context = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
                Assert.Equal(tenantB, context.Database.GetConnectionString());
                await context.Database.EnsureCreatedAsync();
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<LegacyProduct>>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await repository.AddAsync(new LegacyProduct
                {
                    Id = Guid.NewGuid(),
                    Name = "Tenant B",
                    Price = 2m,
                });
                await unitOfWork.SaveChangesAsync();
            }

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                TestTenantConnectionStringResolver.ConnectionString.Value = tenantA;
                LegacyDbContext context = scope.ServiceProvider.GetRequiredService<LegacyDbContext>();
                var repository = scope.ServiceProvider.GetRequiredService<IRepository<LegacyProduct>>();
                List<LegacyProduct> products = await repository.GetAll().ToListAsync();
                LegacyProduct product = Assert.Single(products);
                Assert.Equal("Tenant A", product.Name);
            }
        }
        finally
        {
            TestTenantConnectionStringResolver.ConnectionString.Value = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TenantConnectionStringResolverFailsClosedWithoutTenant()
    {
        var resolver = new TestTenantConnectionStringResolver();
        TestTenantConnectionStringResolver.ConnectionString.Value = null;

        Assert.Throws<InvalidOperationException>(() => resolver.ResolveConnectionString());
    }

    private sealed class TestTenantConnectionStringResolver : ITenantConnectionStringResolver
    {
        public static readonly AsyncLocal<string?> ConnectionString = new();

        public string ResolveConnectionString()
        {
            return ConnectionString.Value
                ?? throw new InvalidOperationException("No tenant connection string is resolved.");
        }
    }
}
