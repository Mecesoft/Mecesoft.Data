using System.Globalization;
using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[Collection("SqlServerStrings")]
public sealed class SqlServerStringTenantTests
{
    private readonly SqlServerStringTestFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerStringTenantTests(
        SqlServerStringTestFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SqlServerFact]
    public async Task StringTenantIdsMustRemainCaseIsolated()
    {
        await using var scope = _fixture.CreateCaseInsensitiveScope("TenantA");
        var entityType = scope.Context.Model.FindEntityType(typeof(SqlStringTenantEntity))
            ?? throw new InvalidOperationException("The string tenant entity is not mapped.");
        string tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The string tenant table is not mapped.");

        Guid upperId = Guid.NewGuid();
        Guid lowerId = Guid.NewGuid();
        string insertSql =
            $"INSERT INTO [{tableName}] ([Id], [TenantId], [Name], [Version]) "
            + "VALUES ({0}, {1}, {2}, 1), ({3}, {4}, {5}, 1)";
        await scope.Context.Database.ExecuteSqlRawAsync(
            insertSql,
            upperId,
            "TenantA",
            "upper",
            lowerId,
            "tenanta",
            "lower");

        long rawCount = await CountRowsAsync(scope.Context, tableName);
        Assert.Equal(2, rawCount);

        scope.SetTenant("TenantA", TenantOperation.Read);
        DataResult<IReadOnlyList<SqlStringTenantEntity>> read =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());

        Assert.True(read.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(read.Failure);
        _output.WriteLine(
            $"Case-insensitive string tenant read failed closed: {read.Failure!.Message}");

        TenantConfigurationException directException =
            await Assert.ThrowsAsync<TenantConfigurationException>(
                () => scope.Context.StringEntities.AsNoTracking().ToListAsync());
        Assert.Contains(
            "collation",
            directException.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task CaseSensitiveStringTenantIdsRemainIsolated()
    {
        await using var scope = _fixture.CreateCaseSensitiveScope("TenantA");

        var upper = new SqlStringTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "TenantA",
            Name = "upper",
        };
        scope.SetTenant("TenantA", TenantOperation.Create);
        scope.Context.StringEntities.Add(upper);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        var lower = new SqlStringTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = "tenanta",
            Name = "lower",
        };
        scope.SetTenant("tenanta", TenantOperation.Create);
        scope.Context.StringEntities.Add(lower);
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        scope.SetTenant("TenantA", TenantOperation.Read);
        DataResult<IReadOnlyList<SqlStringTenantEntity>> upperRows =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());
        Assert.True(upperRows.IsSuccess, upperRows.Failure?.ToString());
        SqlStringTenantEntity upperRow = Assert.Single(upperRows.Value);
        Assert.Equal("TenantA", upperRow.TenantId);
        Assert.Equal("upper", upperRow.Name);

        scope.SetTenant("tenanta", TenantOperation.Read);
        DataResult<IReadOnlyList<SqlStringTenantEntity>> lowerRows =
            await scope.ReadRepository.ListAsync(new AllStringSpecification());
        Assert.True(lowerRows.IsSuccess, lowerRows.Failure?.ToString());
        SqlStringTenantEntity lowerRow = Assert.Single(lowerRows.Value);
        Assert.Equal("tenanta", lowerRow.TenantId);
        Assert.Equal("lower", lowerRow.Name);

        _output.WriteLine("Case-sensitive string tenant isolation verified.");
    }

    private static async Task<long> CountRowsAsync(
        MecesoftDbContext context,
        string tableName)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{tableName}]";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private sealed class AllStringSpecification : ISpecification<SqlStringTenantEntity>
    {
        public Expression<Func<SqlStringTenantEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<SqlStringTenantEntity>> Sorts => [];
    }
}
