using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class CustomMappingTrustTests
{
    [Fact]
    public async Task MappingMutationAfterModelCreationCannotRedirectTenantProperty()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var mapping = new MutableOrderTenantMapping();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            mapping);
        await context.Database.EnsureCreatedAsync();
        _ = context.Model;

        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();

        // Mutate the custom mapping after the model has captured the tenant property.
        mapping.TenantPropertyName = nameof(Order.Id);
        mapping.UseIdSelector = true;

        store.Clear();
        Assert.True(store.TrySet(tenantA, TenantOperation.Create));
        store.AuthorizeWriteForTesting(tenantA, TenantOperation.Create);

        context.Orders.Add(new Order
        {
            Id = tenantA,
            TenantId = tenantB,
            Name = "forged",
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<TenantConfigurationFailure>(exception.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.Orders.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class MutableOrderTenantMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<Order, Guid> _inner =
            new(order => order.TenantId);

        public Type EntityType => typeof(Order);

        public Type TenantIdType { get; set; } = typeof(Guid);

        public string TenantPropertyName { get; set; } = nameof(Order.TenantId);

        public bool UseIdSelector { get; set; }

        public object? GetTenantId(object entity)
            => UseIdSelector
                ? ((Order)entity).Id
                : _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }
}
