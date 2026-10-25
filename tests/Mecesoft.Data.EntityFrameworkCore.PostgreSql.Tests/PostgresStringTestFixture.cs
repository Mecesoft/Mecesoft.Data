using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[CollectionDefinition("PostgreSqlStrings", DisableParallelization = true)]
public sealed class PostgresStringTestGroup : ICollectionFixture<PostgresStringTestFixture>
{
}

public sealed class PostgresStringTestFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _caseSensitive;
    private PostgresTestDatabase? _caseInsensitive;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _caseSensitive = await PostgresTestDatabase.CreateAsync();
        await _caseSensitive.EnsureCreatedAsync<PostgresStringTestDbContext>(
            options => new PostgresStringTestDbContext(
                options,
                new TenantScopeStore(),
                PostgresStringTestMappings.TenantMappings(),
                []));

        _caseInsensitive = await PostgresTestDatabase.CreateAsync();
        await _caseInsensitive.ExecuteAsync(
            $"CREATE COLLATION {PostgresCaseInsensitiveStringTestDbContext.CaseInsensitiveCollation} "
            + "(provider = icu, locale = 'und-u-ks-level2', deterministic = false)");
        await _caseInsensitive.EnsureCreatedAsync<PostgresCaseInsensitiveStringTestDbContext>(
            options => new PostgresCaseInsensitiveStringTestDbContext(
                options,
                new TenantScopeStore(),
                PostgresStringTestMappings.TenantMappings(),
                []));
    }

    public async Task DisposeAsync()
    {
        if (_caseSensitive is not null)
        {
            await _caseSensitive.DisposeAsync();
        }

        if (_caseInsensitive is not null)
        {
            await _caseInsensitive.DisposeAsync();
        }
    }

    internal PostgresStringTestScope<PostgresStringTestDbContext>
        CreateCaseSensitiveScope(string tenantId)
    {
        PostgresTestDatabase database = _caseSensitive
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");
        var store = new TenantScopeStore();
        PostgresStringTestDbContext context = new(
            database.Options<PostgresStringTestDbContext>(),
            store,
            PostgresStringTestMappings.TenantMappings(),
            []);
        return new PostgresStringTestScope<PostgresStringTestDbContext>(
            context,
            store,
            tenantId);
    }

    internal PostgresStringTestScope<PostgresCaseInsensitiveStringTestDbContext>
        CreateCaseInsensitiveScope(string tenantId)
    {
        PostgresTestDatabase database = _caseInsensitive
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");
        var store = new TenantScopeStore();
        PostgresCaseInsensitiveStringTestDbContext context = new(
            database.Options<PostgresCaseInsensitiveStringTestDbContext>(),
            store,
            PostgresStringTestMappings.TenantMappings(),
            []);
        return new PostgresStringTestScope<PostgresCaseInsensitiveStringTestDbContext>(
            context,
            store,
            tenantId);
    }
}

internal sealed class PostgresStringTestScope<TContext> : IAsyncDisposable
    where TContext : MecesoftDbContext
{
    public PostgresStringTestScope(TContext context, TenantScopeStore store, string tenantId)
    {
        Context = context;
        Store = store;
        Resolver = new TestPgStringTenantResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<PgStringTenantEntity, string>(
            Resolver,
            Store);
        ReadRepository =
            new EfReadRepository<TContext, PgStringTenantEntity, Guid, string>(
                context,
                Coordinator,
                item => item.Id);
    }

    public TContext Context { get; }

    public TenantScopeStore Store { get; }

    public TestPgStringTenantResolver Resolver { get; }

    public TenantScopeCoordinator<PgStringTenantEntity, string> Coordinator { get; }

    public EfReadRepository<TContext, PgStringTenantEntity, Guid, string> ReadRepository { get; }

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

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
    }
}

internal sealed class TestPgStringTenantResolver(string tenantId)
    : ITenantAccessResolver<PgStringTenantEntity, string>
{
    public string TenantId { get; set; } = tenantId;

    public Expression<Func<PgStringTenantEntity, string>> TenantIdSelector
        => item => item.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<string>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(new TenantScope<string>(TenantId, operation)));
    }

    public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        PgStringTenantEntity root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(
                string.Equals(root.TenantId, TenantId, StringComparison.Ordinal)));
    }
}
