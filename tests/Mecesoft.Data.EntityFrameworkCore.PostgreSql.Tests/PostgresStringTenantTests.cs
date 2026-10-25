using System.Globalization;
using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

[Collection("PostgreSqlStrings")]
public sealed class PostgresStringTenantTests
{
    private readonly PostgresStringTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PostgresStringTenantTests(
        PostgresStringTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [PostgresFact]
    public async Task CaseSensitiveStringTenantIdsRemainIsolated()
    {
        await using var scope = _fixture.CreateCaseSensitiveScope("TenantA");

        scope.SetTenant("TenantA", TenantOperation.Create);
        scope.Context.StringEntities.Add(new PgStringTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "TenantA",
            Name = "upper",
        });
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant("tenanta", TenantOperation.Create);
        scope.Context.StringEntities.Add(new PgStringTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "tenanta",
            Name = "lower",
        });
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant("TenantA", TenantOperation.Read);
        DataResult<IReadOnlyList<PgStringTenantEntity>> upperRows =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());
        Assert.True(upperRows.IsSuccess, upperRows.Failure?.ToString());
        PgStringTenantEntity upperRow = Assert.Single(upperRows.Value);
        Assert.Equal("TenantA", upperRow.TenantId);
        Assert.Equal("upper", upperRow.Name);

        scope.SetTenant("tenanta", TenantOperation.Read);
        DataResult<IReadOnlyList<PgStringTenantEntity>> lowerRows =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());
        Assert.True(lowerRows.IsSuccess, lowerRows.Failure?.ToString());
        PgStringTenantEntity lowerRow = Assert.Single(lowerRows.Value);
        Assert.Equal("tenanta", lowerRow.TenantId);
        Assert.Equal("lower", lowerRow.Name);

        _output.WriteLine("Deterministic PostgreSQL collation kept string tenants isolated.");
    }

    [PostgresFact]
    public async Task CaseInsensitiveCollationStringTenantFailsClosed()
    {
        await using var scope = _fixture.CreateCaseInsensitiveScope("TenantA");
        var entityType = scope.Context.Model.FindEntityType(typeof(PgStringTenantEntity))
            ?? throw new InvalidOperationException("The string tenant entity is not mapped.");
        string schema = entityType.GetSchema() ?? "public";
        string tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The string tenant table is not mapped.");
        string qualifiedTable = $"\"{schema}\".\"{tableName}\"";

        Guid upperId = Guid.NewGuid();
        Guid lowerId = Guid.NewGuid();
        string insertSql =
            $"INSERT INTO {qualifiedTable} (\"Id\", \"TenantId\", \"Name\") "
            + "VALUES ({0}, {1}, {2}), ({3}, {4}, {5})";
        await scope.Context.Database.ExecuteSqlRawAsync(
            insertSql,
            upperId,
            "TenantA",
            "upper",
            lowerId,
            "tenanta",
            "lower");

        long rawCount = await CountRowsAsync(scope.Context, qualifiedTable);
        Assert.Equal(2, rawCount);

        long unsafeMatches = await CountTenantMatchesAsync(
            scope.Context,
            qualifiedTable,
            "TenantA");
        Assert.Equal(2, unsafeMatches);

        scope.SetTenant("TenantA", TenantOperation.Read);
        DataResult<IReadOnlyList<PgStringTenantEntity>> read =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());

        Assert.True(read.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(read.Failure);
        _output.WriteLine(
            $"Non-deterministic string tenant read failed closed: {read.Failure!.Message}");

        TenantConfigurationException directException =
            await Assert.ThrowsAsync<TenantConfigurationException>(
                () => scope.Context.StringEntities.AsNoTracking().ToListAsync());
        Assert.Contains(
            "collation",
            directException.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> CountRowsAsync(
        MecesoftDbContext context,
        string qualifiedTable)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {qualifiedTable}";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountTenantMatchesAsync(
        MecesoftDbContext context,
        string qualifiedTable,
        string tenantId)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM {qualifiedTable} WHERE \"TenantId\" = @tenant";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private sealed class AllStringSpecification : ISpecification<PgStringTenantEntity>
    {
        public Expression<Func<PgStringTenantEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<PgStringTenantEntity>> Sorts => [];
    }
}
