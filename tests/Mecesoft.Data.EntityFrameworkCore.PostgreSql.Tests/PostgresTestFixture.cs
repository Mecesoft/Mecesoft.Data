using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[CollectionDefinition("PostgreSql", DisableParallelization = true)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresTestFixture>
{
}

public sealed class PostgresTestFixture : IAsyncLifetime
{
    private PostgresTestDatabase? _database;
    private PostgresTestDatabase? _xminDatabase;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)))
        {
            return;
        }

        _database = await PostgresTestDatabase.CreateAsync();
        await _database.EnsureCreatedAsync<PostgresTestDbContext>(options => new PostgresTestDbContext(
            options,
            new TenantScopeStore(),
            PostgresTestMappings.TenantMappings(),
            []));

        _xminDatabase = await PostgresTestDatabase.CreateAsync();
        await _xminDatabase.EnsureCreatedAsync<PostgresXminTestDbContext>(
            options => new PostgresXminTestDbContext(
                options,
                new TenantScopeStore(),
                PostgresTestMappings.XminTenantMappings(),
                []));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (_xminDatabase is not null)
        {
            await _xminDatabase.DisposeAsync();
        }
    }

    internal PostgresTestScope CreateScope(
        Guid tenantId,
        Func<PostgresTestDbContext, TenantScopeStore, IUnitOfWorkCommitParticipant[]>? participantsFactory = null)
    {
        if (_database is null)
        {
            throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");
        }

        return new PostgresTestScope(_database, tenantId, participantsFactory);
    }

    internal PostgresXminTestScope CreateXminScope(Guid tenantId)
    {
        if (_xminDatabase is null)
        {
            throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");
        }

        return new PostgresXminTestScope(_xminDatabase, tenantId);
    }
}

internal sealed class PostgresTestDatabase
{
    private const string DatabasePrefix = "mecesoft_data_pg_";

    private readonly string _adminConnectionString;
    private readonly string _connectionString;
    private readonly string _databaseName;

    private PostgresTestDatabase(
        string adminConnectionString,
        string connectionString,
        string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        _connectionString = connectionString;
        _databaseName = databaseName;
    }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        string rawConnectionString =
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)
            ?? throw new InvalidOperationException(
                $"Set {PostgresFactAttribute.ConnectionVariable} before running PostgreSQL tests.");

        var builder = new NpgsqlConnectionStringBuilder(rawConnectionString)
        {
            ApplicationName = "Mecesoft.Data.PostgreSql.Tests",
        };

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            builder.Database = "postgres";
        }

        string adminConnectionString = builder.ConnectionString;
        string databaseName = $"{DatabasePrefix}{Guid.NewGuid():N}";
        builder.Database = databaseName;

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new PostgresTestDatabase(
            adminConnectionString,
            builder.ConnectionString,
            databaseName);
    }

    public DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(_connectionString)
            .Options;
    }

    public DbContextOptions<TContext> OptionsWithProductionTenancy<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(_connectionString)
            .UseMecesoftPostgreSqlTenancy()
            .Options;
    }

    public DbContextOptions<TContext> OptionsWithProductionTenancyAndSearchPath<TContext>(
        string searchPath)
        where TContext : DbContext
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = searchPath,
        };

        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseMecesoftPostgreSqlTenancy()
            .Options;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task EnsureCreatedAsync<TContext>(
        Func<DbContextOptions<TContext>, TContext> createContext,
        bool productionTenancy = false)
        where TContext : DbContext
    {
        DbContextOptions<TContext> options = productionTenancy
            ? OptionsWithProductionTenancy<TContext>()
            : Options<TContext>();
        await using TContext context = createContext(options);
        await context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Only temporary PostgreSQL test databases may be dropped.");
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
    }
}

internal sealed class PostgresTestScope : IAsyncDisposable
{
    public PostgresTestScope(
        PostgresTestDatabase database,
        Guid tenantId,
        Func<PostgresTestDbContext, TenantScopeStore, IUnitOfWorkCommitParticipant[]>? participantsFactory)
    {
        Store = new TenantScopeStore();
        Context = new PostgresTestDbContext(
            database.Options<PostgresTestDbContext>(),
            Store,
            PostgresTestMappings.TenantMappings(),
            []);
        Resolver = new TestPgTenantResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<PgOrder, Guid>(Resolver, Store);
        IUnitOfWorkCommitParticipant[] participants =
            participantsFactory?.Invoke(Context, Store) ?? [];
        UnitOfWork = new EfUnitOfWork<PostgresTestDbContext>(Context, participants);
        Authorizer = new TestPgSoftDeleteAuthorizer();
        ReadRepository = new EfReadRepository<PostgresTestDbContext, PgOrder, Guid, Guid>(
            Context,
            Coordinator,
            order => order.Id);
        WriteRepository = new EfWriteRepository<PostgresTestDbContext, PgOrder, Guid, Guid>(
            Context,
            Coordinator,
            UnitOfWork,
            order => order.Id);
        SoftDeleteRepository =
            new EfSoftDeleteRepository<PostgresTestDbContext, PgOrder, Guid, Guid>(
                Context,
                Coordinator,
                UnitOfWork,
                Authorizer,
                order => order.Id);
    }

    public TenantScopeStore Store { get; }

    public PostgresTestDbContext Context { get; }

    public TestPgTenantResolver Resolver { get; }

    public TenantScopeCoordinator<PgOrder, Guid> Coordinator { get; }

    public EfUnitOfWork<PostgresTestDbContext> UnitOfWork { get; }

    public TestPgSoftDeleteAuthorizer Authorizer { get; }

    public EfReadRepository<PostgresTestDbContext, PgOrder, Guid, Guid> ReadRepository { get; }

    public EfWriteRepository<PostgresTestDbContext, PgOrder, Guid, Guid> WriteRepository { get; }

    public EfSoftDeleteRepository<PostgresTestDbContext, PgOrder, Guid, Guid> SoftDeleteRepository { get; }

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

    public static ConcurrencyToken Token(PgOrder order)
    {
        return new ConcurrencyToken(order.Version.ToString("D"));
    }

    public async ValueTask DisposeAsync()
    {
        await UnitOfWork.DisposeAsync();
        await Context.DisposeAsync();
    }
}

internal sealed class PostgresXminTestScope : IAsyncDisposable
{
    public PostgresXminTestScope(PostgresTestDatabase database, Guid tenantId)
    {
        Store = new TenantScopeStore();
        Context = new PostgresXminTestDbContext(
            database.Options<PostgresXminTestDbContext>(),
            Store,
            PostgresTestMappings.XminTenantMappings(),
            []);
        Resolver = new TestPgXminTenantResolver(tenantId);
        Coordinator = new TenantScopeCoordinator<PgXminOrder, Guid>(Resolver, Store);
        UnitOfWork = new EfUnitOfWork<PostgresXminTestDbContext>(Context);
    }

    public TenantScopeStore Store { get; }

    public PostgresXminTestDbContext Context { get; }

    public TestPgXminTenantResolver Resolver { get; }

    public TenantScopeCoordinator<PgXminOrder, Guid> Coordinator { get; }

    public EfUnitOfWork<PostgresXminTestDbContext> UnitOfWork { get; }

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

    public async ValueTask DisposeAsync()
    {
        await UnitOfWork.DisposeAsync();
        await Context.DisposeAsync();
    }
}

internal sealed class TestPgTenantResolver(Guid tenantId) : ITenantAccessResolver<PgOrder, Guid>
{
    public Expression<Func<PgOrder, Guid>> TenantIdSelector => order => order.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(new TenantScope<Guid>(tenantId, operation)));
    }

    public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        PgOrder root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == tenantId));
    }
}

internal sealed class TestPgXminTenantResolver(Guid tenantId) : ITenantAccessResolver<PgXminOrder, Guid>
{
    public Expression<Func<PgXminOrder, Guid>> TenantIdSelector => order => order.TenantId;

    public DataResult<bool> ValidateConfiguration() => DataResult.Succeeded(true);

    public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            DataResult.Succeeded(new TenantScope<Guid>(tenantId, operation)));
    }

    public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
        PgXminOrder root,
        TenantOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == tenantId));
    }
}

internal sealed class TestPgSoftDeleteAuthorizer : ISoftDeleteAuthorizer<PgOrder, Guid, Guid>
{
    public bool Allow { get; set; } = true;

    public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
        SoftDeleteAuthorizationContext<PgOrder, Guid, Guid> context,
        CancellationToken cancellationToken = default)
    {
        return Allow
            ? ValueTask.FromResult(DataResult.Succeeded(context.Operation))
            : ValueTask.FromResult(
                DataResult.Failed<SoftDeleteOperation>(
                    new AuthorizationFailure("soft_delete", "Denied by test authorizer.")));
    }
}

internal sealed class PgTenantChangingParticipant(
    TenantScopeStore store,
    PostgresTestDbContext context,
    Guid otherTenant) : IUnitOfWorkCommitParticipant
{
    public ValueTask<DataResult<bool>> BeforeCommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        store.Clear();
        _ = store.TrySet(otherTenant, TenantOperation.Create);
        context.Orders.Add(new PgOrder
        {
            Id = Guid.NewGuid(),
            TenantId = otherTenant,
            Name = "other-tenant",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return ValueTask.FromResult(DataResult.Succeeded(true));
    }
}

internal sealed class PgThrowingParticipant : IUnitOfWorkCommitParticipant
{
    public ValueTask<DataResult<bool>> BeforeCommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("The test participant failed.");
    }
}
