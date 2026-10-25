using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerTenantCollationRulesTests
{
    [Theory]
    [InlineData("Latin1_General_100_BIN2")]
    [InlineData("Latin1_General_100_BIN2_UTF8")]
    [InlineData("SQL_Latin1_General_CP1_BIN2")]
    public void Bin2CollationsAreSupported(string collation)
    {
        Assert.True(SqlServerTenantCollationRules.IsSupportedBin2Collation(collation));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Latin1_General_100_CS_AS")]
    [InlineData("Latin1_General_100_CS_AI")]
    [InlineData("Latin1_General_100_CI_AS")]
    [InlineData("Latin1_General_100_CS_AS_KS_WS")]
    [InlineData("Latin1_General_100_BIN")]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    public void NonBin2CollationsAreRejected(string? collation)
    {
        Assert.False(SqlServerTenantCollationRules.IsSupportedBin2Collation(collation));
    }

    [Theory]
    [InlineData("nvarchar", 231, false, true)]
    [InlineData("varchar", 167, false, false)]
    [InlineData("varchar", 167, true, false)]
    [InlineData("nvarchar", 231, true, false)]
    [InlineData("sysname", 231, false, false)]
    [InlineData("char", 175, false, false)]
    [InlineData("nchar", 239, false, false)]
    [InlineData("text", 35, false, false)]
    [InlineData("ntext", 99, false, false)]
    public void OnlyNvarcharSystemTypeIsAccepted(
        string typeName,
        int systemTypeId,
        bool isUserDefined,
        bool expected)
    {
        Assert.Equal(
            expected,
            SqlServerTenantCollationRules.IsSupportedStringType(
                typeName,
                systemTypeId,
                isUserDefined));
    }

    [Theory]
    [InlineData("nvarchar", "Latin1_General_100_BIN2", true)]
    [InlineData("varchar", "Latin1_General_100_BIN2_UTF8", false)]
    [InlineData("varchar", "Latin1_General_100_BIN2", false)]
    [InlineData("nvarchar", "Latin1_General_100_BIN2_UTF8", false)]
    [InlineData("varchar", "Latin1_General_100_CS_AS", false)]
    [InlineData(null, "Latin1_General_100_BIN2", false)]
    public void OnlyLosslessTypeAndCollationPairsAreSupported(
        string? typeName,
        string? collation,
        bool expected)
    {
        Assert.Equal(
            expected,
            SqlServerTenantCollationRules.IsSupportedEncoding(typeName, collation));
    }

    [Theory]
    [InlineData("nvarchar", -1, true)]
    [InlineData("nvarchar", 256, true)]
    [InlineData("nvarchar", 255, false)]
    [InlineData("varchar", -1, false)]
    [InlineData("varchar", 512, false)]
    [InlineData("varchar", 128, false)]
    [InlineData("char", 512, false)]
    public void TenantColumnCapacityMustCoverTheMaximumCanonicalIdentifier(
        string typeName,
        int maxLength,
        bool expected)
    {
        Assert.Equal(
            expected,
            SqlServerTenantCollationRules.HasSufficientCapacity(typeName, maxLength));
    }

    [Theory]
    [InlineData("U", true)]
    [InlineData("V", false)]
    [InlineData("SN", false)]
    [InlineData("ET", false)]
    [InlineData("IF", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyUserTablesAreSupported(string? objectType, bool expected)
    {
        Assert.Equal(
            expected,
            SqlServerTenantCollationRules.IsSupportedObjectType(objectType));
    }

    [Fact]
    public void ProductionVerifierIsRegisteredAsSingletonByDiHelper()
    {
        var services = new ServiceCollection();
        services.AddMecesoftSqlServerTenantCollationVerification();

        using ServiceProvider provider = services.BuildServiceProvider();
        ITenantCollationVerifier verifier =
            provider.GetRequiredService<ITenantCollationVerifier>();

        Assert.Same(SqlServerTenantCollationVerifier.Instance, verifier);
        Assert.Same(
            verifier,
            provider.GetRequiredService<ITenantCollationVerifier>());
    }

    [Fact]
    public void CustomStringMappingWithoutExactMarkerFailsClosed()
    {
        var options = new DbContextOptionsBuilder<SqlServerCustomMappingStringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .UseMecesoftSqlServerTenancy()
            .Options;

        using var context = new SqlServerCustomMappingStringDbContext(
            options,
            new TenantScopeStore(),
            [new NonExactStringMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "ITenantExactMapping",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionVerifierRequiresExactnessConfiguration()
    {
        var options = new DbContextOptionsBuilder<SqlServerBin2StringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .Options;

        using var context = new SqlServerBin2StringDbContext(
            options,
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "exact tenant equality",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CombinedConfigurationRejectsUntrustedVerifier()
    {
        var options = new DbContextOptionsBuilder<SqlServerBin2StringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .UseMecesoftTenantSecurity(
                new UntrustedTenantCollationVerifier(),
                SqlServerTenantFilterExpressionAugmenter.Instance)
            .Options;

        using var context = new SqlServerBin2StringDbContext(
            options,
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "trusted",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoOpAugmenterIsNotAcceptedAsSqlServerExactness()
    {
        var options = new DbContextOptionsBuilder<SqlServerBin2StringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .UseMecesoftTenantSecurity(
                SqlServerTenantCollationVerifier.Instance,
                new NoOpTenantFilterAugmenter())
            .Options;

        using var context = new SqlServerBin2StringDbContext(
            options,
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "built-in",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "SqlServerTenantFilterExpressionAugmenter",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FilterDroppingAugmenterFailsModelCreation()
    {
        var options = new DbContextOptionsBuilder<SqlServerBin2StringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .UseMecesoftTenantSecurity(
                SqlServerTenantCollationVerifier.Instance,
                new FilterDroppingTenantFilterAugmenter())
            .Options;

        using var context = new SqlServerBin2StringDbContext(
            options,
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "removed the direct tenant equality",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongPropertyStringMappingFailsBeforeSqlServerVerifier()
    {
        var options = new DbContextOptionsBuilder<SqlServerBin2StringDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,14336;Database=unused;User Id=sa;"
                + "Password=unused;TrustServerCertificate=True;Encrypt=True;")
            .UseMecesoftSqlServerTenancy()
            .Options;

        using var safe = new SqlServerBin2StringDbContext(
            options,
            new TenantScopeStore(),
            SqlServerProductionStringMappings.Bin2TenantMappings(),
            []);
        _ = safe.Model;

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => new SqlServerBin2StringDbContext(
                    options,
                    new TenantScopeStore(),
                    [new WrongPropertyStringMapping()],
                    []));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoOpTenantFilterAugmenter
        : ITenantFilterExpressionAugmenter
    {
        public LambdaExpression Augment(
            MecesoftDbContext context,
            ITenantEntityMapping mapping,
            LambdaExpression tenantFilter)
            => tenantFilter;
    }

    private sealed class FilterDroppingTenantFilterAugmenter
        : ITenantFilterExpressionAugmenter
    {
        public LambdaExpression Augment(
            MecesoftDbContext context,
            ITenantEntityMapping mapping,
            LambdaExpression tenantFilter)
            => Expression.Lambda(
                Expression.Constant(true),
                tenantFilter.Parameters[0]);
    }

    private sealed class NonExactStringMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<SqlStringTenantEntity, string> _inner =
            new(entity => entity.TenantId);

        public Type EntityType => typeof(SqlStringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(SqlStringTenantEntity.TenantId);

        public object? GetTenantId(object entity)
            => _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class WrongPropertyStringMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<SqlStringTenantEntity, string> _inner =
            new(entity => entity.TenantId);

        public Type EntityType => typeof(SqlStringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(SqlStringTenantEntity.Name);

        public object? GetTenantId(object entity)
            => ((SqlStringTenantEntity)entity).Name;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class UntrustedTenantCollationVerifier
        : ITenantCollationVerifier
    {
        public DataResult<bool> Verify(
            MecesoftDbContext context,
            ITenantEntityMapping mapping)
        {
            return DataResult.Succeeded(true);
        }
    }
}
