using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[CollectionDefinition("PostgreSqlProduction", DisableParallelization = true)]
public sealed class PostgresProductionTenancyGroup
    : ICollectionFixture<PostgresProductionTenancyFixture>
{
}

public sealed class PostgresProductionTenancyFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _safe;
    private PostgresTestDatabase? _unsafe;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _safe = await PostgresTestDatabase.CreateAsync();
        await _safe.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS tenancy;");
        await _safe.EnsureCreatedAsync<PostgresSafeProductionDbContext>(
            options => new PostgresSafeProductionDbContext(
                options,
                new TenantScopeStore(),
                PostgresProductionTestMappings.SafeMappings(),
                PostgresProductionTestMappings.SoftDeleteMappings()),
            productionTenancy: true);

        _unsafe = await PostgresTestDatabase.CreateAsync();
        await _unsafe.ExecuteAsync(
            "CREATE EXTENSION IF NOT EXISTS citext; "
            + "CREATE COLLATION mecesoft_ci "
            + "(provider = icu, locale = 'und-u-ks-level2', deterministic = false); "
            + "CREATE DOMAIN tenant_domain AS text;");
        await _unsafe.EnsureCreatedAsync<PostgresUnsafeProductionDbContext>(
            options => new PostgresUnsafeProductionDbContext(
                options,
                new TenantScopeStore(),
                PostgresProductionTestMappings.UnsafeMappings(),
                []),
            productionTenancy: true);
    }

    public async Task DisposeAsync()
    {
        if (_safe is not null)
        {
            await _safe.DisposeAsync();
        }

        if (_unsafe is not null)
        {
            await _unsafe.DisposeAsync();
        }
    }

    public PostgresSafeProductionDbContext CreateSafeContext()
        => CreateSafeContext(new TenantScopeStore());

    internal PostgresSafeProductionDbContext CreateSafeContext(
        TenantScopeStore store)
    {
        PostgresTestDatabase database = _safe
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");

        return new PostgresSafeProductionDbContext(
            database.OptionsWithProductionTenancy<PostgresSafeProductionDbContext>(),
            store,
            PostgresProductionTestMappings.SafeMappings(),
            PostgresProductionTestMappings.SoftDeleteMappings());
    }

    internal PostgresProductionOrderScope CreateOrderScope(string tenantId)
    {
        PostgresTestDatabase database = _safe
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");

        return new PostgresProductionOrderScope(database, tenantId);
    }

    public PostgresUnsafeProductionDbContext CreateUnsafeContext()
    {
        PostgresTestDatabase database = _unsafe
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");

        return new PostgresUnsafeProductionDbContext(
            database.OptionsWithProductionTenancy<PostgresUnsafeProductionDbContext>(),
            new TenantScopeStore(),
            PostgresProductionTestMappings.UnsafeMappings(),
            []);
    }
}

internal sealed class PostgresProductionOrderScope : IAsyncDisposable
{
    public PostgresProductionOrderScope(
        PostgresTestDatabase database,
        string tenantId)
    {
        Store = new TenantScopeStore();
        Context = new PostgresSafeProductionDbContext(
            database.OptionsWithProductionTenancy<PostgresSafeProductionDbContext>(),
            Store,
            PostgresProductionTestMappings.SafeMappings(),
            PostgresProductionTestMappings.SoftDeleteMappings());
        Resolver = new TestPgProductionOrderResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<PgProductionOrder, string>(
            Resolver,
            Store);
        UnitOfWork = new EfUnitOfWork<PostgresSafeProductionDbContext>(Context, []);
        ReadRepository =
            new EfReadRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>(
                Context,
                Coordinator,
                order => order.Id);
        WriteRepository =
            new EfWriteRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>(
                Context,
                Coordinator,
                UnitOfWork,
                order => order.Id);
        SoftDeleteRepository =
            new EfSoftDeleteRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>(
                Context,
                Coordinator,
                UnitOfWork,
                new TestPgProductionOrderAuthorizer(),
                order => order.Id);
    }

    public TenantScopeStore Store { get; }

    public PostgresSafeProductionDbContext Context { get; }

    public TestPgProductionOrderResolver Resolver { get; }

    public TenantScopeCoordinator<PgProductionOrder, string> Coordinator { get; }

    public EfUnitOfWork<PostgresSafeProductionDbContext> UnitOfWork { get; }

    public EfReadRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>
        ReadRepository { get; }

    public EfWriteRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>
        WriteRepository { get; }

    public EfSoftDeleteRepository<PostgresSafeProductionDbContext, PgProductionOrder, Guid, string>
        SoftDeleteRepository { get; }

    public void SetTenant(string tenantId, TenantOperation operation)
    {
        Resolver.TenantId = tenantId;
        Store.Clear();
        if (!Store.TrySet(tenantId, operation))
        {
            throw new InvalidOperationException("The test tenant could not be set.");
        }

        if (operation == TenantOperation.Read)
        {
            Context.AuthorizeReadForTesting(tenantId);
        }
        else
        {
            Store.AuthorizeWriteForTesting(tenantId, operation);
        }
    }

    public static ConcurrencyToken Token(PgProductionOrder order)
    {
        return new ConcurrencyToken(order.Version.ToString("D"));
    }

    public async ValueTask DisposeAsync()
    {
        await UnitOfWork.DisposeAsync();
        await Context.DisposeAsync();
    }
}

internal sealed class TestPgProductionOrderResolver(string tenantId)
    : ITenantAccessResolver<PgProductionOrder, string>
{
    public string TenantId { get; set; } = tenantId;

    public Expression<Func<PgProductionOrder, string>> TenantIdSelector
        => order => order.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<string>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(new TenantScope<string>(TenantId, operation)));
    }

    public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        PgProductionOrder root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(
                string.Equals(root.TenantId, TenantId, StringComparison.Ordinal)));
    }
}

internal sealed class TestPgProductionOrderAuthorizer
    : ISoftDeleteAuthorizer<PgProductionOrder, Guid, string>
{
    public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
        SoftDeleteAuthorizationContext<PgProductionOrder, Guid, string> context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(DataResult.Succeeded(context.Operation));
    }
}
