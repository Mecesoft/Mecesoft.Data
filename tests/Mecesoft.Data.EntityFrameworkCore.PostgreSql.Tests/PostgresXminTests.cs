using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Concurrency;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[Collection("PostgreSql")]
public sealed class PostgresXminTests
{
    private readonly PostgresTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresXminTests(
        PostgresTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [PostgresFact]
    public async Task XminIsMappedAsUIntConcurrencyToken()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateXminScope(tenantId);

        IEntityType entityType = scope.Context.Model.FindEntityType(typeof(PgXminOrder))
            ?? throw new InvalidOperationException("The xmin entity is not mapped.");
        IProperty token = Assert.Single(
            entityType.GetProperties(),
            property => property.IsConcurrencyToken);

        Assert.Equal("Version", token.Name);
        Assert.Equal(typeof(uint), token.ClrType);
        Assert.NotNull(token.PropertyInfo);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, token.ValueGenerated);

        _output.WriteLine(
            $"xmin token: name={token.Name}, clr={token.ClrType.Name}, "
            + $"generated={token.ValueGenerated}");
    }

    [PostgresFact]
    public async Task XminRepositoryConstructionFailsClosed()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateXminScope(tenantId);

        ConcurrencyConfigurationException exception =
            Assert.Throws<ConcurrencyConfigurationException>(
                () => new EfWriteRepository<
                    PostgresXminTestDbContext,
                    PgXminOrder,
                    Guid,
                    Guid>(
                    scope.Context,
                    scope.Coordinator,
                    scope.UnitOfWork,
                    order => order.Id));

        _output.WriteLine($"xmin repository rejected: {exception.Message}");
        Assert.Contains(
            "uint",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task XminDirectSaveChangesFailsClosed()
    {
        Guid tenantId = Guid.NewGuid();
        await using var scope = _fixture.CreateXminScope(tenantId);

        scope.SetTenant(tenantId, TenantOperation.Create);
        scope.Context.Orders.Add(new PgXminOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "xmin-direct",
        });

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => scope.Context.SaveChangesAsync());
        Assert.IsType<ConcurrencyConfigurationFailure>(exception.Failure);
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant(tenantId, TenantOperation.Read);
        Assert.Empty(await scope.Context.Orders.AsNoTracking().ToListAsync());
    }
}
