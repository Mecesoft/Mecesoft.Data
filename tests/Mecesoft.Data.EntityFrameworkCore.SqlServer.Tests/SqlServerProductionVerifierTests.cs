using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

[Collection("SqlServerProduction")]
public sealed class SqlServerProductionVerifierTests
{
    private readonly SqlServerProductionVerifierFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SqlServerProductionVerifierTests(
        SqlServerProductionVerifierFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SqlServerFact]
    public void Bin2VerifierAcceptsProductionMapping()
    {
        using var context = _fixture.CreateBin2Context();
        ITenantEntityMapping mapping =
            SqlServerProductionStringMappings.TenantMappings()[0];

        DataResult<bool> result =
            SqlServerTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(result.IsSuccess, result.Failure?.ToString());
    }

    [SqlServerFact]
    public void Bin2StartupValidationSucceedsWithoutTenantScope()
    {
        using var context = _fixture.CreateBin2Context();

        context.ValidateTenantSecurity();
    }

    [SqlServerFact]
    public void QuotedSchemaAndColumnNameResolveToTheProductionVerifier()
    {
        using var context = _fixture.CreateQuotedSchemaContext();

        context.ValidateTenantSecurity();

        _output.WriteLine(
            "Production verifier resolved [tenancy].[Tenant Orders].[Tenant Id].");
    }

    [SqlServerFact]
    public void CharTenantColumnFailsClosed()
    {
        using var context = _fixture.CreateCharContext();

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "unsupported SQL Server type",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public void NonUtf8VarcharTenantColumnFailsClosed()
    {
        using var context = _fixture.CreateEncodingUnsafeContext();
        ITenantEntityMapping mapping =
            SqlServerProductionStringMappings.UnsafeEncodingMappings()[0];

        DataResult<bool> result =
            SqlServerTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(result.IsFailure);
        Assert.Contains(
            "unsupported SQL Server type",
            result.Failure!.Message,
            StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(result.Failure.Message);
    }

    [SqlServerFact]
    public void Utf8VarcharTenantColumnFailsClosedByPolicy()
    {
        using var context = _fixture.CreateEncodingUnsafeContext();
        ITenantEntityMapping mapping =
            SqlServerProductionStringMappings.UnsafeEncodingMappings()[1];

        DataResult<bool> result =
            SqlServerTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(result.IsFailure);
        Assert.Contains(
            "unsupported SQL Server type",
            result.Failure!.Message,
            StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(result.Failure.Message);
    }

    [SqlServerFact]
    public void NarrowNvarcharTenantColumnFailsClosed()
    {
        using var context = _fixture.CreateEncodingUnsafeContext();
        ITenantEntityMapping mapping =
            SqlServerProductionStringMappings.UnsafeEncodingMappings()[2];

        DataResult<bool> result =
            SqlServerTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(result.IsFailure);
        Assert.Contains(
            "capacity",
            result.Failure!.Message,
            StringComparison.OrdinalIgnoreCase);
        _output.WriteLine(result.Failure.Message);
    }

    [SqlServerFact]
    public async Task Bin2CollationStillTreatsTrailingSpaceVariantsAsEqual()
    {
        await using var context = _fixture.CreateBin2Context();
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [TenantEntities] ([Id], [tenant_id], [Name], [Version])
            VALUES ({0}, {1}, {2}, 1), ({3}, {4}, {5}, 1)
            """,
            Guid.NewGuid(),
            "tenant-a",
            "canonical",
            Guid.NewGuid(),
            "tenant-a ",
            "trailing-space");

        await context.Database.OpenConnectionAsync();
        await using var command = context.Database
            .GetDbConnection()
            .CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM [TenantEntities] WHERE [tenant_id] = @tenant";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tenant";
        parameter.Value = "tenant-a";
        command.Parameters.Add(parameter);

        long matches = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);

        _output.WriteLine(
            $"BIN2 trailing-space equality matched {matches} rows for 'tenant-a'.");
        Assert.Equal(2, matches);
    }

    [SqlServerFact]
    public void ProductionVerifierRejectsLinguisticCollationAcceptedByReferenceVerifier()
    {
        using var context = _fixture.CreateCaseSensitiveReferenceContext();
        ITenantEntityMapping mapping =
            SqlServerStringTestMappings.TenantMappings()[0];

        DataResult<bool> reference =
            SqlServerColumnCollationVerifier.Instance.Verify(context, mapping);
        DataResult<bool> production =
            SqlServerTenantCollationVerifier.Instance.Verify(context, mapping);

        Assert.True(reference.IsSuccess, reference.Failure?.ToString());
        Assert.True(production.IsFailure);
        Assert.Contains(
            "_BIN2",
            production.Failure!.Message,
            StringComparison.OrdinalIgnoreCase);

        _output.WriteLine(
            "Reference verifier accepted CS_AS; production verifier rejected it.");
    }
}
