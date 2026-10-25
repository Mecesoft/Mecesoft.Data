using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[CollectionDefinition("SqlServerStrings", DisableParallelization = true)]
public sealed class SqlServerStringTestGroup : ICollectionFixture<SqlServerStringTestFixture>
{
}

public sealed class SqlServerStringTestFixture : IAsyncLifetime
{
    private SqlServerTestDatabase? _caseInsensitive;
    private SqlServerTestDatabase? _caseSensitive;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _caseInsensitive = await SqlServerTestDatabase.CreateAsync();
        await _caseInsensitive.EnsureCreatedAsync<SqlServerCaseInsensitiveStringDbContext>(options =>
            new SqlServerCaseInsensitiveStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerStringTestMappings.TenantMappings(),
                []));

        _caseSensitive = await SqlServerTestDatabase.CreateAsync();
        await _caseSensitive.EnsureCreatedAsync<SqlServerCaseSensitiveStringDbContext>(options =>
            new SqlServerCaseSensitiveStringDbContext(
                options,
                new TenantScopeStore(),
                SqlServerStringTestMappings.TenantMappings(),
                []));
    }

    public async Task DisposeAsync()
    {
        if (_caseInsensitive is not null)
        {
            await _caseInsensitive.DisposeAsync<SqlServerCaseInsensitiveStringDbContext>(options =>
                new SqlServerCaseInsensitiveStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerStringTestMappings.TenantMappings(),
                    []));
        }

        if (_caseSensitive is not null)
        {
            await _caseSensitive.DisposeAsync<SqlServerCaseSensitiveStringDbContext>(options =>
                new SqlServerCaseSensitiveStringDbContext(
                    options,
                    new TenantScopeStore(),
                    SqlServerStringTestMappings.TenantMappings(),
                    []));
        }
    }

    internal SqlServerStringTestScope<SqlServerCaseInsensitiveStringDbContext>
        CreateCaseInsensitiveScope(string tenantId)
    {
        SqlServerTestDatabase database = _caseInsensitive
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");
        var store = new TenantScopeStore();
        var context = new SqlServerCaseInsensitiveStringDbContext(
            database.Options<SqlServerCaseInsensitiveStringDbContext>(),
            store,
            SqlServerStringTestMappings.TenantMappings(),
            []);
        return new SqlServerStringTestScope<SqlServerCaseInsensitiveStringDbContext>(
            context,
            store,
            tenantId);
    }

    internal SqlServerStringTestScope<SqlServerCaseSensitiveStringDbContext>
        CreateCaseSensitiveScope(string tenantId)
    {
        SqlServerTestDatabase database = _caseSensitive
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");
        var store = new TenantScopeStore();
        var context = new SqlServerCaseSensitiveStringDbContext(
            database.Options<SqlServerCaseSensitiveStringDbContext>(),
            store,
            SqlServerStringTestMappings.TenantMappings(),
            []);
        return new SqlServerStringTestScope<SqlServerCaseSensitiveStringDbContext>(
            context,
            store,
            tenantId);
    }
}

internal sealed class SqlServerStringTestScope<TContext> : IAsyncDisposable
    where TContext : MecesoftDbContext
{
    public SqlServerStringTestScope(
        TContext context,
        TenantScopeStore store,
        string tenantId)
    {
        Context = context;
        Store = store;
        Resolver = new TestStringTenantResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<SqlStringTenantEntity, string>(
            Resolver,
            store);
        ReadRepository =
            new EfReadRepository<TContext, SqlStringTenantEntity, Guid, string>(
                context,
                Coordinator,
                item => item.Id);
    }

    public TContext Context { get; }

    public TenantScopeStore Store { get; }

    public TestStringTenantResolver Resolver { get; }

    public TenantScopeCoordinator<SqlStringTenantEntity, string> Coordinator { get; }

    public EfReadRepository<TContext, SqlStringTenantEntity, Guid, string> ReadRepository { get; }

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

internal sealed class TestStringTenantResolver(string tenantId)
    : ITenantAccessResolver<SqlStringTenantEntity, string>
{
    public string TenantId { get; set; } = tenantId;

    public Expression<Func<SqlStringTenantEntity, string>> TenantIdSelector
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
        SqlStringTenantEntity root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(
                string.Equals(root.TenantId, TenantId, StringComparison.Ordinal)));
    }
}
