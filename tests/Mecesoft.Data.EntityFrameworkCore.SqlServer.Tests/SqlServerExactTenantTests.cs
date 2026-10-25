using System.Globalization;
using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.Abstractions.Transactions;
using Mecesoft.Data.EntityFrameworkCore.Queries;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Transactions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[Collection("SqlServerProduction")]
public sealed class SqlServerExactTenantTests
{
    private readonly SqlServerProductionVerifierFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerExactTenantTests(
        SqlServerProductionVerifierFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SqlServerFact]
    public async Task ExactFilterProtectsDirectLinqMaterializationAndFilteredQueries()
    {
        string tenantId = NewTenantId();
        await using SqlServerExactStringScope scope = CreateScope(tenantId);
        SeedIds ids = await SeedAsync(scope, tenantId);

        scope.SetTenant(tenantId, TenantOperation.Read);

        List<SqlStringTenantEntity> rows = await scope.Context.StringEntities
            .AsNoTracking()
            .ToListAsync();
        SqlStringTenantEntity row = Assert.Single(rows);
        Assert.Equal(ids.CanonicalId, row.Id);
        Assert.Equal(tenantId, row.TenantId);

        Assert.Equal(
            1,
            await scope.Context.StringEntities.CountAsync());
        Assert.False(
            await scope.Context.StringEntities.AnyAsync(
                entity => entity.Name == "trailing"));

        long rawMatches = await CountRawTenantMatchesAsync(
            scope.Context,
            "TenantEntities",
            tenantId);
        Assert.Equal(2, rawMatches);
        _output.WriteLine(
            $"Raw tenant matches: {rawMatches}; exact-filtered matches: 1.");

        string sql = scope.Context.StringEntities
            .Where(entity => entity.Name == "anything")
            .ToQueryString();
        Assert.Contains("LEN", sql, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(sql);
    }

    [SqlServerFact]
    public async Task ReadProjectionCountAnyAndPaginationCannotSeeTrailingTenantRow()
    {
        string tenantId = NewTenantId();
        await using SqlServerExactStringScope scope = CreateScope(tenantId);
        SeedIds ids = await SeedAsync(scope, tenantId);
        scope.SetTenant(tenantId, TenantOperation.Read);

        DataResult<SqlStringTenantEntity> canonical =
            await scope.ReadRepository.GetByIdAsync(ids.CanonicalId);
        Assert.True(canonical.IsSuccess, canonical.Failure?.ToString());

        DataResult<SqlStringTenantEntity> trailing =
            await scope.ReadRepository.GetByIdAsync(ids.TrailingId);
        Assert.True(trailing.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(trailing.Failure);

        DataResult<StringDto> trailingProjection =
            await scope.ReadRepository.GetByIdAsync(
                ids.TrailingId,
                new StringProjection());
        Assert.True(trailingProjection.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(trailingProjection.Failure);

        DataResult<IReadOnlyList<SqlStringTenantEntity>> list =
            await scope.ReadRepository.ListAsync(
                new AllSpecification<SqlStringTenantEntity>());
        Assert.True(list.IsSuccess, list.Failure?.ToString());
        Assert.Single(list.Value);

        DataResult<IReadOnlyList<StringDto>> projected =
            await scope.ReadRepository.ListAsync(
                new StringProjectionSpecification());
        Assert.True(projected.IsSuccess, projected.Failure?.ToString());
        Assert.Single(projected.Value);

        DataResult<long> count = await scope.ReadRepository.CountAsync(
            new AllSpecification<SqlStringTenantEntity>());
        Assert.True(count.IsSuccess, count.Failure?.ToString());
        Assert.Equal(1, count.Value);

        DataResult<bool> anyTrailing = await scope.ReadRepository.AnyAsync(
            new NameSpecification("trailing"));
        Assert.True(anyTrailing.IsSuccess, anyTrailing.Failure?.ToString());
        Assert.False(anyTrailing.Value);

        DataResult<PageResult<StringDto>> page =
            await scope.ReadRepository.PageAsync(
                new StringProjectionSpecification(),
                new PageRequest(1, 10));
        Assert.True(page.IsSuccess, page.Failure?.ToString());
        Assert.Single(page.Value.Items);
        Assert.Equal(1, page.Value.TotalCount);
    }

    [SqlServerFact]
    public async Task UpdateCannotLoadTrailingTenantRow()
    {
        string tenantId = NewTenantId();
        await using SqlServerExactStringScope scope = CreateScope(tenantId);
        SeedIds ids = await SeedAsync(scope, tenantId);
        scope.SetTenant(tenantId, TenantOperation.Update);

        Assert.True(
            (await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions())).IsSuccess);

        DataResult<EntityUpdateResult<SqlStringTenantEntity>> canonical =
            await scope.WriteRepository.UpdateAsync(
                ids.CanonicalId,
                new ConcurrencyToken("1"),
                static (entity, _) =>
                {
                    entity.Name = "canonical-updated";
                    return ValueTask.FromResult(
                        DataResult.Succeeded(EntityMutationResult.Completed));
                });
        Assert.True(canonical.IsSuccess, canonical.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True(
            (await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions())).IsSuccess);
        DataResult<EntityUpdateResult<SqlStringTenantEntity>> trailing =
            await scope.WriteRepository.UpdateAsync(
                ids.TrailingId,
                new ConcurrencyToken("1"),
                static (_, _) => ValueTask.FromResult(
                    DataResult.Succeeded(EntityMutationResult.Completed)));
        Assert.True(trailing.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(trailing.Failure);
        Assert.Equal(
            DataTransactionState.Active,
            scope.UnitOfWork.CurrentTransaction!.State);
        Assert.True((await scope.UnitOfWork.RollbackAsync()).IsSuccess);
    }

    [SqlServerFact]
    public async Task SoftDeleteAndRestoreCannotReachTrailingTenantRow()
    {
        string tenantId = NewTenantId();
        await using SqlServerExactStringScope scope = CreateScope(tenantId);
        SeedIds ids = await SeedAsync(scope, tenantId);
        scope.SetTenant(tenantId, TenantOperation.SoftDelete);

        Assert.True(
            (await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions())).IsSuccess);
        DataResult<SqlStringSoftDeleteEntity> deleted =
            await scope.SoftDeleteRepository.SoftDeleteAsync(
                ids.CanonicalSoftId,
                new ConcurrencyToken("1"));
        Assert.True(deleted.IsSuccess, deleted.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        string restoreToken = deleted.Value.Version.ToString(
            CultureInfo.InvariantCulture);
        Assert.True(
            (await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions())).IsSuccess);
        DataResult<SqlStringSoftDeleteEntity> restored =
            await scope.SoftDeleteRepository.RestoreAsync(
                ids.CanonicalSoftId,
                new ConcurrencyToken(restoreToken));
        Assert.True(restored.IsSuccess, restored.Failure?.ToString());
        Assert.True((await scope.UnitOfWork.CommitAsync()).IsSuccess);

        Assert.True(
            (await scope.UnitOfWork.BeginTransactionAsync(
                new TransactionOptions())).IsSuccess);
        DataResult<SqlStringSoftDeleteEntity> trailing =
            await scope.SoftDeleteRepository.SoftDeleteAsync(
                ids.TrailingSoftId,
                new ConcurrencyToken("1"));
        Assert.True(trailing.IsFailure);
        Assert.IsType<NotFoundFailure<Guid>>(trailing.Failure);
        Assert.Equal(
            DataTransactionState.RolledBack,
            scope.UnitOfWork.CurrentTransaction!.State);

        scope.SetTenant(tenantId, TenantOperation.Read);
        using (SoftDeleteReadAuthorization.Begin(
            tenantId,
            SoftDeleteOperation.SoftDelete))
        {
            Assert.Equal(
                1,
                await scope.Context.SoftDeleteEntities
                    .IgnoreSoftDelete()
                    .CountAsync());
        }
    }

    [SqlServerFact]
    public async Task LenSuffixPredicateHandlesNullUnicodeAndLimitCases()
    {
        using var context = _fixture.CreateBin2Context(new TenantScopeStore());
        await context.Database.OpenConnectionAsync();

        const string tenant = "tenant-a";
        long asciiCount = await CountExactPredicateAsync(
            context,
            "VALUES (N'tenant-a'), (N'tenant-a '), (N''), (CAST(NULL AS nvarchar(128)))",
            tenant);
        Assert.Equal(1, asciiCount);

        const string unicodeTenant = "tënant-ü";
        long unicodeCount = await CountExactPredicateAsync(
            context,
            "VALUES (N'tënant-ü'), (N'tënant-ü '), (N'tënant-ü\u00A0')",
            unicodeTenant);
        Assert.Equal(1, unicodeCount);

        string max = new('a', 128);
        string tooLong = new('a', 129);
        long limitCount = await CountExactPredicateAsync(
            context,
            $"VALUES (N'{max}'), (N'{tooLong}'), (N'{max} ')",
            max);
        Assert.Equal(1, limitCount);

        _output.WriteLine(
            "LEN suffix predicate excluded NULL, trailing-space, Unicode-whitespace, empty, and over-limit rows.");
    }

    [SqlServerFact]
    public async Task MaximumAndUnicodeTenantIdsRoundTripWithoutLoss()
    {
        var store = new TenantScopeStore();
        await using var context = _fixture.CreateBin2Context(store);
        string maximum = new('t', TenantIdValidation.MaxStringLength);
        string unicode = "Tënant-Ω-🙂";
        string questionMark = "Tënant-?-??";

        foreach (string tenantId in new[] { maximum, unicode, questionMark })
        {
            Guid id = Guid.NewGuid();
            store.Clear();
            Assert.True(store.TrySet(tenantId, TenantOperation.Create));
            store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
            context.StringEntities.Add(new SqlStringTenantEntity
            {
                Id = id,
                TenantId = tenantId,
                Name = "capacity-and-unicode",
                Version = 1,
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            store.Clear();
            Assert.True(store.TrySet(tenantId, TenantOperation.Read));
            context.AuthorizeReadForTesting(tenantId);
            SqlStringTenantEntity row = await context.StringEntities
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == id);
            Assert.Equal(tenantId, row.TenantId);
            context.ChangeTracker.Clear();
        }

        _output.WriteLine(
            "SQL Server nvarchar + _BIN2 preserved maximum, Unicode, and '?' tenant IDs.");
    }

    [SqlServerFact]
    public void Utf8VarcharTenantColumnIsRejectedByProductionPolicy()
    {
        using var context = _fixture.CreateUtf8Context(new TenantScopeStore());

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "unsupported SQL Server type",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(exception.Message);
    }

    private SqlServerExactStringScope CreateScope(string tenantId)
    {
        var store = new TenantScopeStore();
        SqlServerBin2StringDbContext context = _fixture.CreateBin2Context(store);
        return new SqlServerExactStringScope(context, store, tenantId);
    }

    private static async Task<SeedIds> SeedAsync(
        SqlServerExactStringScope scope,
        string tenantId)
    {
        Guid canonicalId = Guid.NewGuid();
        Guid trailingId = Guid.NewGuid();
        Guid canonicalSoftId = Guid.NewGuid();
        Guid trailingSoftId = Guid.NewGuid();

        scope.Store.Clear();
        Assert.True(scope.Store.TrySet(tenantId, TenantOperation.Create));
        scope.Store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);
        scope.Context.StringEntities.Add(new SqlStringTenantEntity
        {
            Id = canonicalId,
            TenantId = tenantId,
            Name = "canonical",
            Version = 1,
        });
        scope.Context.SoftDeleteEntities.Add(new SqlStringSoftDeleteEntity
        {
            Id = canonicalSoftId,
            TenantId = tenantId,
            Name = "canonical-soft",
            Version = 1,
        });
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        await scope.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [TenantEntities] ([Id], [tenant_id], [Name], [Version]) "
            + "VALUES ({0}, {1}, {2}, 1)",
            trailingId,
            tenantId + " ",
            "trailing");
        await scope.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO [SoftDeleteTenantEntities] "
            + "([Id], [tenant_id], [Name], [IsDeleted], [DeletedAtUtc], [Version]) "
            + "VALUES ({0}, {1}, {2}, 0, NULL, 1)",
            trailingSoftId,
            tenantId + " ",
            "trailing-soft");

        return new SeedIds(
            canonicalId,
            trailingId,
            canonicalSoftId,
            trailingSoftId);
    }

    private static string NewTenantId()
        => $"tenant-{Guid.NewGuid():N}";

    private static async Task<long> CountRawTenantMatchesAsync(
        DbContext context,
        string tableName,
        string tenantId)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database
            .GetDbConnection()
            .CreateCommand();
        command.CommandText =
            $"SELECT COUNT_BIG(*) FROM [{tableName}] WHERE [tenant_id] = @tenant";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountExactPredicateAsync(
        DbContext context,
        string valuesClause,
        string tenantId)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database
            .GetDbConnection()
            .CreateCommand();
        command.CommandText =
            $"SELECT COUNT_BIG(*) FROM ({valuesClause}) AS v(TenantId) "
            + "WHERE TenantId = @tenant "
            + "AND LEN(TenantId + N'x') = LEN(@tenant + N'x')";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private sealed record SeedIds(
        Guid CanonicalId,
        Guid TrailingId,
        Guid CanonicalSoftId,
        Guid TrailingSoftId);

    private sealed record StringDto(
        Guid Id,
        string TenantId,
        string Name);

    private sealed class StringProjection : IProjection<SqlStringTenantEntity, StringDto>
    {
        public Expression<Func<SqlStringTenantEntity, StringDto>> Selector =>
            entity => new StringDto(entity.Id, entity.TenantId, entity.Name);
    }

    private sealed class AllSpecification<TEntity> : ISpecification<TEntity>
    {
        public Expression<Func<TEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<TEntity>> Sorts => [];
    }

    private sealed class NameSpecification : ISpecification<SqlStringTenantEntity>
    {
        public NameSpecification(string name)
        {
            Criteria = entity => entity.Name == name;
        }

        public Expression<Func<SqlStringTenantEntity, bool>>? Criteria { get; }

        public IReadOnlyList<ISortExpression<SqlStringTenantEntity>> Sorts => [];
    }

    private sealed class StringProjectionSpecification
        : IProjectionSpecification<SqlStringTenantEntity, StringDto>
    {
        public Expression<Func<SqlStringTenantEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<SqlStringTenantEntity>> Sorts =>
        [
            new SortExpression<SqlStringTenantEntity, Guid>(
                entity => entity.Id,
                SortDirection.Ascending),
        ];

        public Expression<Func<SqlStringTenantEntity, StringDto>> Selector =>
            entity => new StringDto(entity.Id, entity.TenantId, entity.Name);
    }

    private sealed class ExactStringTenantResolver<TEntity>
        : ITenantAccessResolver<TEntity, string>
        where TEntity : class, ITenantEntity<string>
    {
        private readonly string _tenantId;
        private readonly Expression<Func<TEntity, string>> _selector;

        public ExactStringTenantResolver(
            string tenantId,
            Expression<Func<TEntity, string>> selector)
        {
            _tenantId = tenantId;
            _selector = selector;
        }

        public Expression<Func<TEntity, string>> TenantIdSelector => _selector;

        public ValueTask<DataResult<TenantScope<string>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    new TenantScope<string>(_tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    string.Equals(
                        root.TenantId,
                        _tenantId,
                        StringComparison.Ordinal)));
        }
    }

    private sealed class AllowSoftDeleteAuthorizer
        : ISoftDeleteAuthorizer<SqlStringSoftDeleteEntity, Guid, string>
    {
        public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
            SoftDeleteAuthorizationContext<SqlStringSoftDeleteEntity, Guid, string> context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(context.Operation));
        }
    }

    private sealed class SqlServerExactStringScope : IAsyncDisposable
    {
        public SqlServerExactStringScope(
            SqlServerBin2StringDbContext context,
            TenantScopeStore store,
            string tenantId)
        {
            Context = context;
            Store = store;
            MainCoordinator =
                new TenantScopeCoordinator<SqlStringTenantEntity, string>(
                    new ExactStringTenantResolver<SqlStringTenantEntity>(
                        tenantId,
                        entity => entity.TenantId),
                    store);
            SoftCoordinator =
                new TenantScopeCoordinator<SqlStringSoftDeleteEntity, string>(
                    new ExactStringTenantResolver<SqlStringSoftDeleteEntity>(
                        tenantId,
                        entity => entity.TenantId),
                    store);
            UnitOfWork = new EfUnitOfWork<SqlServerBin2StringDbContext>(context);
            ReadRepository =
                new EfReadRepository<
                    SqlServerBin2StringDbContext,
                    SqlStringTenantEntity,
                    Guid,
                    string>(
                    context,
                    MainCoordinator,
                    entity => entity.Id);
            WriteRepository =
                new EfWriteRepository<
                    SqlServerBin2StringDbContext,
                    SqlStringTenantEntity,
                    Guid,
                    string>(
                    context,
                    MainCoordinator,
                    UnitOfWork,
                    entity => entity.Id);
            SoftDeleteRepository =
                new EfSoftDeleteRepository<
                    SqlServerBin2StringDbContext,
                    SqlStringSoftDeleteEntity,
                    Guid,
                    string>(
                    context,
                    SoftCoordinator,
                    UnitOfWork,
                    new AllowSoftDeleteAuthorizer(),
                    entity => entity.Id);
        }

        public SqlServerBin2StringDbContext Context { get; }

        public TenantScopeStore Store { get; }

        public TenantScopeCoordinator<SqlStringTenantEntity, string> MainCoordinator { get; }

        public TenantScopeCoordinator<SqlStringSoftDeleteEntity, string> SoftCoordinator { get; }

        public EfUnitOfWork<SqlServerBin2StringDbContext> UnitOfWork { get; }

        public EfReadRepository<
            SqlServerBin2StringDbContext,
            SqlStringTenantEntity,
            Guid,
            string> ReadRepository { get; }

        public EfWriteRepository<
            SqlServerBin2StringDbContext,
            SqlStringTenantEntity,
            Guid,
            string> WriteRepository { get; }

        public EfSoftDeleteRepository<
            SqlServerBin2StringDbContext,
            SqlStringSoftDeleteEntity,
            Guid,
            string> SoftDeleteRepository { get; }

        public void SetTenant(string tenantId, TenantOperation operation)
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
}
