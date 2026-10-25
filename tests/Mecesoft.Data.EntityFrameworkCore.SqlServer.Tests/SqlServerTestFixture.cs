using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[CollectionDefinition("SqlServer", DisableParallelization = true)]
public sealed class SqlServerTestGroup : ICollectionFixture<SqlServerTestFixture>
{
}

public sealed class SqlServerTestFixture : IAsyncLifetime
{
    private SqlServerTestDatabase? _database;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _database = await SqlServerTestDatabase.CreateAsync();
        await _database.EnsureCreatedAsync<SqlServerTestDbContext>(options => new SqlServerTestDbContext(
            options,
            new TenantScopeStore(),
            SqlServerTestMappings.TenantMappings(),
            []));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync<SqlServerTestDbContext>(options => new SqlServerTestDbContext(
                options,
                new TenantScopeStore(),
                SqlServerTestMappings.TenantMappings(),
                []));
        }
    }

    internal SqlServerTestScope CreateScope(
        Guid tenantId,
        Func<SqlServerTestDbContext, TenantScopeStore, IUnitOfWorkCommitParticipant[]>? participantsFactory = null)
    {
        if (_database is null)
        {
            throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");
        }

        return new SqlServerTestScope(_database, tenantId, participantsFactory);
    }
}

internal sealed class SqlServerTestDatabase
{
    private readonly string _connectionString;

    private SqlServerTestDatabase(string connectionString)
    {
        _connectionString = connectionString;
    }

    public static async Task<SqlServerTestDatabase> CreateAsync()
    {
        string rawConnectionString =
            Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionVariable)
            ?? throw new InvalidOperationException(
                $"Set {SqlServerFactAttribute.ConnectionVariable} before running SQL Server tests.");

        var builder = new SqlConnectionStringBuilder(rawConnectionString)
        {
            InitialCatalog = $"MecesoftDataSqlServer_{Guid.NewGuid():N}",
            ApplicationName = "Mecesoft.Data.SqlServer.Tests",
        };

        return new SqlServerTestDatabase(builder.ConnectionString);
    }

    public DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(_connectionString)
            .UseMecesoftTenantFilterAugmentation(
                SqlServerTenantFilterExpressionAugmenter.Instance)
            .Options;
    }

    public DbContextOptions<TContext> OptionsWithRetryingExecutionStrategy<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(_connectionString, options => options.EnableRetryOnFailure(3))
            .Options;
    }

    public DbContextOptions<TContext> OptionsWithProductionTenancy<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(_connectionString)
            .UseMecesoftSqlServerTenancy()
            .Options;
    }

    public async Task EnsureCreatedAsync<TContext>(
        Func<DbContextOptions<TContext>, TContext> createContext)
        where TContext : DbContext
    {
        await using TContext context = createContext(Options<TContext>());
        await context.Database.EnsureCreatedAsync();
    }

    public async Task CreateDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog = "master",
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE [{new SqlConnectionStringBuilder(_connectionString).InitialCatalog}]";
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync<TContext>(
        Func<DbContextOptions<TContext>, TContext> createContext)
        where TContext : DbContext
    {
        SqlConnection.ClearAllPools();
        await using TContext context = createContext(Options<TContext>());
        await context.Database.EnsureDeletedAsync();
        SqlConnection.ClearAllPools();
    }
}

internal sealed class SqlServerTestScope : IAsyncDisposable
{
    public SqlServerTestScope(
        SqlServerTestDatabase database,
        Guid tenantId,
        Func<SqlServerTestDbContext, TenantScopeStore, IUnitOfWorkCommitParticipant[]>? participantsFactory)
    {
        Store = new TenantScopeStore();
        Context = new SqlServerTestDbContext(
            database.Options<SqlServerTestDbContext>(),
            Store,
            SqlServerTestMappings.TenantMappings(),
            []);
        Resolver = new TestSqlTenantResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<SqlOrder, Guid>(Resolver, Store);
        IUnitOfWorkCommitParticipant[] participants =
            participantsFactory?.Invoke(Context, Store) ?? [];
        UnitOfWork = new EfUnitOfWork<SqlServerTestDbContext>(Context, participants);
        Authorizer = new TestSoftDeleteAuthorizer();
        ReadRepository = new EfReadRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid>(
            Context,
            Coordinator,
            order => order.Id);
        WriteRepository = new EfWriteRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid>(
            Context,
            Coordinator,
            UnitOfWork,
            order => order.Id);
        SoftDeleteRepository =
            new EfSoftDeleteRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid>(
                Context,
                Coordinator,
                UnitOfWork,
                Authorizer,
                order => order.Id);
    }

    public TenantScopeStore Store { get; }

    public SqlServerTestDbContext Context { get; }

    public TestSqlTenantResolver Resolver { get; }

    public TenantScopeCoordinator<SqlOrder, Guid> Coordinator { get; }

    public EfUnitOfWork<SqlServerTestDbContext> UnitOfWork { get; }

    public TestSoftDeleteAuthorizer Authorizer { get; }

    public EfReadRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid> ReadRepository { get; }

    public EfWriteRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid> WriteRepository { get; }

    public EfSoftDeleteRepository<SqlServerTestDbContext, SqlOrder, Guid, Guid> SoftDeleteRepository { get; }

    public void SetTenant(Guid tenantId, TenantOperation operation)
    {
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

    public void SetStringTenant(string tenantId, TenantOperation operation)
    {
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

    public static ConcurrencyToken Token(SqlOrder order)
    {
        return new ConcurrencyToken(Convert.ToBase64String(order.Version));
    }

    public async ValueTask DisposeAsync()
    {
        await UnitOfWork.DisposeAsync();
        await Context.DisposeAsync();
    }
}

internal sealed class TestSqlTenantResolver(Guid tenantId) : ITenantAccessResolver<SqlOrder, Guid>
{
    public Expression<Func<SqlOrder, Guid>> TenantIdSelector => order => order.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(new TenantScope<Guid>(tenantId, operation)));
    }

    public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        SqlOrder root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == tenantId));
    }
}

internal sealed class TestSoftDeleteAuthorizer : ISoftDeleteAuthorizer<SqlOrder, Guid, Guid>
{
    public bool Allow { get; set; } = true;

    public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
        SoftDeleteAuthorizationContext<SqlOrder, Guid, Guid> context,
        CancellationToken cancellationToken = default)
    {
        return Allow
            ? ValueTask.FromResult(DataResult.Succeeded(context.Operation))
            : ValueTask.FromResult(
                DataResult.Failed<SoftDeleteOperation>(
                    new AuthorizationFailure("soft_delete", "Denied by test authorizer.")));
    }
}

internal sealed class TenantChangingParticipant(
    TenantScopeStore store,
    SqlServerTestDbContext context,
    Guid otherTenant) : IUnitOfWorkCommitParticipant
{
    public ValueTask<DataResult<bool>> BeforeCommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        store.Clear();
        _ = store.TrySet(otherTenant, TenantOperation.Create);
        context.Orders.Add(new SqlOrder
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenant,
            Name = "other-tenant",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return ValueTask.FromResult(DataResult.Succeeded(true));
    }
}

internal sealed class ThrowingParticipant : IUnitOfWorkCommitParticipant
{
    public ValueTask<DataResult<bool>> BeforeCommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The test participant failed.");
    }
}
