using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[Collection("PostgreSqlProduction")]
public sealed class PostgresProductionVerifierTests
{
    private readonly PostgresProductionTenancyFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresProductionVerifierTests(
        PostgresProductionTenancyFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [PostgresFact]
    public void SafeMappingsPassProductionVerificationAndStartup()
    {
        using var context = _fixture.CreateSafeContext();

        Assert.True(context.HasTenantCollationVerification);

        foreach (ITenantEntityMapping mapping in
            PostgresProductionTestMappings.SafeMappings())
        {
            DataResult<bool> result =
                PostgreSqlTenantCollationVerifier.Instance.Verify(
                    context,
                    mapping);
            Assert.True(result.IsSuccess, result.Failure?.ToString());
        }

        context.ValidateTenantSecurity();
        _output.WriteLine(
            "Deterministic text/varchar, quoted schema, and HasColumnName passed.");
    }

    [PostgresFact]
    public void UnsafeTypesAndNonDeterministicCollationFailClosed()
    {
        using var context = _fixture.CreateUnsafeContext();

        foreach (ITenantEntityMapping mapping in
            PostgresProductionTestMappings.UnsafeMappings())
        {
            DataResult<bool> result =
                PostgreSqlTenantCollationVerifier.Instance.Verify(
                    context,
                    mapping);
            Assert.True(result.IsFailure, mapping.EntityType.Name);
            _output.WriteLine(
                $"{mapping.EntityType.Name}: {result.Failure!.Message}");
        }

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [PostgresFact]
    public void NarrowVarcharTenantColumnFailsClosed()
    {
        using var context = _fixture.CreateUnsafeContext();
        ITenantEntityMapping mapping =
            PostgresProductionTestMappings.UnsafeMappings()
                .Single(item => item.EntityType == typeof(PgProductionNarrowVarcharEntity));

        DataResult<bool> result =
            PostgreSqlTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(result.IsFailure);
        Assert.Contains(
            "capacity",
            result.Failure!.Message,
            StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(result.Failure.Message);
    }

    [PostgresFact]
    public async Task MaximumAndUnicodeTenantIdsRoundTripWithoutLoss()
    {
        var store = new TenantScopeStore();
        await using var context = _fixture.CreateSafeContext(store);

        string maximum = new('t', TenantIdValidation.MaxStringLength);
        string unicode = "Tënant-Ω-🙂";
        Guid maximumId = Guid.NewGuid();
        Guid unicodeId = Guid.NewGuid();

        store.Clear();
        Assert.True(store.TrySet(maximum, TenantOperation.Create));
        store.AuthorizeWriteForTesting(maximum, TenantOperation.Create);
        context.TextEntities.Add(new PgProductionTextEntity
        {
            Id = maximumId,
            TenantId = maximum,
            Name = "maximum",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        Assert.True(store.TrySet(unicode, TenantOperation.Create));
        store.AuthorizeWriteForTesting(unicode, TenantOperation.Create);
        context.VarcharEntities.Add(new PgProductionVarcharEntity
        {
            Id = unicodeId,
            TenantId = unicode,
            Name = "unicode",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        store.Clear();
        Assert.True(store.TrySet(maximum, TenantOperation.Read));
        context.AuthorizeReadForTesting(maximum);
        PgProductionTextEntity maximumRow = await context.TextEntities
            .AsNoTracking()
            .SingleAsync(item => item.Id == maximumId);
        Assert.Equal(maximum, maximumRow.TenantId);

        store.Clear();
        Assert.True(store.TrySet(unicode, TenantOperation.Read));
        context.AuthorizeReadForTesting(unicode);
        PgProductionVarcharEntity unicodeRow = await context.VarcharEntities
            .AsNoTracking()
            .SingleAsync(item => item.Id == unicodeId);
        Assert.Equal(unicode, unicodeRow.TenantId);

        _output.WriteLine(
            "PostgreSQL preserved a 128-character tenant ID and Unicode without loss.");
    }

    [PostgresFact]
    public async Task StringTenantRepositoriesKeepIsolationAndSoftDeleteUnderProductionTenancy()
    {
        string tenantA = $"Tenant-A-{Guid.NewGuid():N}";
        string tenantB = $"Tenant-B-{Guid.NewGuid():N}";
        await using var scopeA = _fixture.CreateOrderScope(tenantA);
        await using var scopeB = _fixture.CreateOrderScope(tenantB);

        PgProductionOrder alpha = await AddOrderAsync(scopeA, tenantA, "alpha");
        PgProductionOrder beta = await AddOrderAsync(scopeA, tenantA, "beta");
        PgProductionOrder gamma = await AddOrderAsync(scopeA, tenantA, "gamma");
        PgProductionOrder other = await AddOrderAsync(scopeB, tenantB, "other");

        Assert.False(scopeA.Context.HasTenantFilterAugmentation);

        scopeA.SetTenant(tenantA, TenantOperation.Create);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<PgProductionOrder> nonCanonical =
            await scopeA.WriteRepository.AddAsync(new PgProductionOrder
            {
                Id = Guid.NewGuid(),
                TenantId = $" {tenantA}",
                Name = "non-canonical",
            });
        Assert.True(nonCanonical.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(nonCanonical.Failure);
        Assert.True((await scopeA.UnitOfWork.RollbackAsync()).IsSuccess);
        scopeA.Context.ChangeTracker.Clear();

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<IReadOnlyList<PgProductionOrder>> list =
            await scopeA.ReadRepository.ListAsync(new AllProductionOrdersSpecification());
        Assert.True(list.IsSuccess, list.Failure?.ToString());
        Assert.Equal(3, list.Value.Count);
        Assert.All(list.Value, order => Assert.Equal(tenantA, order.TenantId));

        DataResult<long> count =
            await scopeA.ReadRepository.CountAsync(new AllProductionOrdersSpecification());
        Assert.True(count.IsSuccess, count.Failure?.ToString());
        Assert.Equal(3, count.Value);

        DataResult<bool> anyGamma =
            await scopeA.ReadRepository.AnyAsync(new OrderNamedSpecification("gamma"));
        Assert.True(anyGamma.IsSuccess, anyGamma.Failure?.ToString());
        Assert.True(anyGamma.Value);

        DataResult<bool> anyOther =
            await scopeA.ReadRepository.AnyAsync(new OrderNamedSpecification("other"));
        Assert.True(anyOther.IsSuccess, anyOther.Failure?.ToString());
        Assert.False(anyOther.Value);

        DataResult<IReadOnlyList<string>> projected =
            await scopeA.ReadRepository.ListAsync(new OrderNamesSpecification());
        Assert.True(projected.IsSuccess, projected.Failure?.ToString());
        Assert.Equal(["alpha", "beta", "gamma"], projected.Value);

        DataResult<PageResult<string>> firstPage =
            await scopeA.ReadRepository.PageAsync(
                new OrderNamesSpecification(),
                new PageRequest(1, 2));
        Assert.True(firstPage.IsSuccess, firstPage.Failure?.ToString());
        Assert.Equal(["alpha", "beta"], firstPage.Value.Items);
        Assert.Equal(3, firstPage.Value.TotalCount);
        Assert.True(firstPage.Value.HasNextPage);

        DataResult<PageResult<string>> secondPage =
            await scopeA.ReadRepository.PageAsync(
                new OrderNamesSpecification(),
                new PageRequest(2, 2));
        Assert.True(secondPage.IsSuccess, secondPage.Failure?.ToString());
        Assert.Equal(["gamma"], secondPage.Value.Items);

        DataResult<PgProductionOrder> crossTenant =
            await scopeA.ReadRepository.GetByIdAsync(other.Id);
        Assert.True(crossTenant.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossTenant.Failure);

        ConcurrencyToken alphaToken = PostgresProductionOrderScope.Token(alpha);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<PgProductionOrder>> updated =
            await scopeA.WriteRepository.UpdateAsync(
                alpha.Id,
                alphaToken,
                (order, _) =>
                {
                    order.Name = "alpha-updated";
                    return ValueTask.FromResult(
                        DataResult.Succeeded(EntityMutationResult.Completed));
                });
        Assert.True(updated.IsSuccess, updated.Failure?.ToString());
        Assert.NotEqual(alphaToken.Value, updated.Value.ConcurrencyToken.Value);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        ConcurrencyToken betaToken = PostgresProductionOrderScope.Token(beta);
        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<PgProductionOrder> deleted =
            await scopeA.SoftDeleteRepository.SoftDeleteAsync(beta.Id, betaToken);
        Assert.True(deleted.IsSuccess, deleted.Failure?.ToString());
        Assert.True(deleted.Value.IsDeleted);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<long> activeCount =
            await scopeA.ReadRepository.CountAsync(new AllProductionOrdersSpecification());
        Assert.True(activeCount.IsSuccess, activeCount.Failure?.ToString());
        Assert.Equal(2, activeCount.Value);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        scopeA.Context.ChangeTracker.Clear();
        List<PgProductionOrder> allRows;
        using (SoftDeleteReadAuthorization.Begin(
            tenantA,
            SoftDeleteOperation.SoftDelete))
        {
            allRows = await scopeA.Context.Orders
                .AsNoTracking()
                .IgnoreSoftDelete()
                .ToListAsync();
        }

        Assert.Equal(3, allRows.Count);
        Assert.Single(allRows, order => order.IsDeleted);

        Assert.True((await scopeB.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<PgProductionOrder> crossDelete =
            await scopeB.SoftDeleteRepository.SoftDeleteAsync(
                gamma.Id,
                PostgresProductionOrderScope.Token(gamma));
        Assert.True(crossDelete.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(crossDelete.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            scopeB.UnitOfWork.CurrentTransaction!.State);

        Assert.True((await scopeA.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<PgProductionOrder> restored =
            await scopeA.SoftDeleteRepository.RestoreAsync(
                beta.Id,
                PostgresProductionOrderScope.Token(deleted.Value));
        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.False(restored.Value.IsDeleted);
        Assert.True((await scopeA.UnitOfWork.CommitAsync()).IsSuccess);

        scopeA.SetTenant(tenantA, TenantOperation.Read);
        DataResult<long> restoredCount =
            await scopeA.ReadRepository.CountAsync(new AllProductionOrdersSpecification());
        Assert.True(restoredCount.IsSuccess, restoredCount.Failure?.ToString());
        Assert.Equal(3, restoredCount.Value);

        scopeB.SetTenant(tenantB, TenantOperation.Read);
        DataResult<long> otherCount =
            await scopeB.ReadRepository.CountAsync(new AllProductionOrdersSpecification());
        Assert.True(otherCount.IsSuccess, otherCount.Failure?.ToString());
        Assert.Equal(1, otherCount.Value);

        _output.WriteLine(
            "String tenant repository reads, projections, paging, update, and soft-delete "
            + "remained isolated under PostgreSQL production tenancy.");
    }

    private static async Task<PgProductionOrder> AddOrderAsync(
        PostgresProductionOrderScope scope,
        string tenantId,
        string name)
    {
        scope.SetTenant(tenantId, TenantOperation.Create);
        Assert.True((await scope.UnitOfWork.BeginTransactionAsync(
            new TransactionOptions())).IsSuccess);
        DataResult<PgProductionOrder> added = await scope.WriteRepository.AddAsync(
            new PgProductionOrder
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = name,
            });
        Assert.True(added.IsSuccess, added.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);
        scope.Context.ChangeTracker.Clear();
        return added.Value;
    }

    private sealed class AllProductionOrdersSpecification
        : ISpecification<PgProductionOrder>
    {
        public Expression<Func<PgProductionOrder, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<PgProductionOrder>> Sorts => [];
    }

    private sealed class OrderNamedSpecification(string name)
        : ISpecification<PgProductionOrder>
    {
        public Expression<Func<PgProductionOrder, bool>>? Criteria =>
            order => order.Name == name;

        public IReadOnlyList<ISortExpression<PgProductionOrder>> Sorts => [];
    }

    private sealed class OrderNamesSpecification
        : IProjectionSpecification<PgProductionOrder, string>
    {
        public Expression<Func<PgProductionOrder, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<PgProductionOrder>> Sorts =>
        [
            new SortExpression<PgProductionOrder, string>(
                order => order.Name,
                SortDirection.Ascending),
        ];

        public Expression<Func<PgProductionOrder, string>> Selector =>
            order => order.Name;
    }
}
