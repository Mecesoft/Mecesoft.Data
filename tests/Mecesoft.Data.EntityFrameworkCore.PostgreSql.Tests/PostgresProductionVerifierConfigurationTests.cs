using System.Linq.Expressions;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class PostgresProductionVerifierConfigurationTests
{
    [Fact]
    public void ProductionVerifierRequiresProductionConfiguration()
    {
        using var context = new PostgresPropertyOnlyProductionDbContext(
            Options(productionTenancy: false),
            new TenantScopeStore(),
            [new TenantEntityMapping<PgProductionTextEntity, string>(
                item => item.TenantId)],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "UseMecesoftPostgreSqlTenancy",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CustomMappingWithoutExactMarkerFailsClosed()
    {
        using var context = new PostgresPropertyOnlyProductionDbContext(
            Options(productionTenancy: true),
            new TenantScopeStore(),
            [new NonExactStringMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "ITenantExactMapping",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FakeExactMarkerWithConstantTrueFilterFailsModelValidation()
    {
        using var context = new ConstantTrueMappingDbContext(
            Options<ConstantTrueMappingDbContext>(),
            new TenantScopeStore(),
            [new FakeExactConstantTrueMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "contains a predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FakeExactMarkerWithOtherPropertyFilterFailsModelValidation()
    {
        using var context = new OtherPropertyMappingDbContext(
            Options<OtherPropertyMappingDbContext>(),
            new TenantScopeStore(),
            [new FakeExactOtherPropertyMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "contains a predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongPropertyStringMappingFailsBeforePostgreSqlVerifier()
    {
        using var safe = new PostgresPropertyOnlyProductionDbContext(
            Options(productionTenancy: true),
            new TenantScopeStore(),
            [new TenantEntityMapping<PgProductionTextEntity, string>(
                item => item.TenantId)],
            []);
        _ = safe.Model;

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => new PostgresPropertyOnlyProductionDbContext(
                    Options(productionTenancy: true),
                    new TenantScopeStore(),
                    [new WrongPropertyStringMapping()],
                    []));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DbContextOptions<PostgresPropertyOnlyProductionDbContext> Options(
        bool productionTenancy)
    {
        DbContextOptionsBuilder<PostgresPropertyOnlyProductionDbContext> builder =
            new DbContextOptionsBuilder<PostgresPropertyOnlyProductionDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;Port=5432;Database=unused;Username=postgres");

        if (productionTenancy)
        {
            builder.UseMecesoftPostgreSqlTenancy();
        }

        return builder.Options;
    }

    private static DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=5432;Database=unused;Username=postgres")
            .UseMecesoftPostgreSqlTenancy()
            .Options;
    }

    private abstract class FakeMappingContextBase : MecesoftDbContext
    {
        protected FakeMappingContextBase(
            DbContextOptions options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping>
                softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<PgProductionTextEntity> TextEntities =>
            Set<PgProductionTextEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => PostgreSqlTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PgProductionTextEntity>(entity =>
            {
                entity.ToTable("TenantEntities");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.TenantId).IsRequired().HasColumnType("text");
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private sealed class ConstantTrueMappingDbContext : FakeMappingContextBase
    {
        public ConstantTrueMappingDbContext(
            DbContextOptions<ConstantTrueMappingDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping>
                softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class OtherPropertyMappingDbContext : FakeMappingContextBase
    {
        public OtherPropertyMappingDbContext(
            DbContextOptions<OtherPropertyMappingDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping>
                softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class NonExactStringMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<PgProductionTextEntity, string> _inner =
            new(item => item.TenantId);

        public Type EntityType => typeof(PgProductionTextEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(PgProductionTextEntity.TenantId);

        public object? GetTenantId(object entity)
            => _inner.GetTenantId(entity);

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class WrongPropertyStringMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<PgProductionTextEntity, string> _inner =
            new(item => item.TenantId);

        public Type EntityType => typeof(PgProductionTextEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(PgProductionTextEntity.Name);

        public object? GetTenantId(object entity)
            => ((PgProductionTextEntity)entity).Name;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class FakeExactConstantTrueMapping
        : ITenantEntityMapping, ITenantExactMapping
    {
        public Type EntityType => typeof(PgProductionTextEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(PgProductionTextEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((PgProductionTextEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
        {
            ParameterExpression parameter =
                Expression.Parameter(typeof(PgProductionTextEntity), "entity");
            return Expression.Lambda(Expression.Constant(true), parameter);
        }
    }

    private sealed class FakeExactOtherPropertyMapping
        : ITenantEntityMapping, ITenantExactMapping
    {
        public Type EntityType => typeof(PgProductionTextEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(PgProductionTextEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((PgProductionTextEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
        {
            ParameterExpression parameter =
                Expression.Parameter(typeof(PgProductionTextEntity), "entity");
            Expression otherProperty = Expression.Property(
                parameter,
                nameof(PgProductionTextEntity.Name));
            Expression currentTenant = Expression.Convert(
                Expression.Property(
                    Expression.Constant(context),
                    nameof(MecesoftDbContext.TenantId)),
                typeof(string));
            return Expression.Lambda(
                Expression.Equal(otherProperty, currentTenant),
                parameter);
        }
    }
}

public sealed class PostgresTenantCollationRulesTests
{
    [Theory]
    [InlineData("text", true)]
    [InlineData("varchar", true)]
    [InlineData("citext", false)]
    [InlineData("bpchar", false)]
    [InlineData("tenant_domain", false)]
    [InlineData(null, false)]
    public void OnlyTextAndVarcharAreSupported(string? typeName, bool expected)
    {
        Assert.Equal(expected, PostgreSqlTenantCollationRules.IsSupportedType(typeName));
    }

    [Theory]
    [InlineData("r", true)]
    [InlineData("p", true)]
    [InlineData("v", false)]
    [InlineData("m", false)]
    [InlineData("f", false)]
    [InlineData(null, false)]
    public void OnlyTablesAndPartitionedTablesAreSupported(
        string? relationKind,
        bool expected)
    {
        Assert.Equal(
            expected,
            PostgreSqlTenantCollationRules.IsSupportedRelationKind(relationKind));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("s", false)]
    [InlineData("v", false)]
    public void GeneratedColumnsAreRejected(string? generation, bool expected)
    {
        Assert.Equal(
            expected,
            PostgreSqlTenantCollationRules.IsSupportedGeneration(generation));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void OnlyDeterministicCollationsAreSupported(
        bool? isDeterministic,
        bool expected)
    {
        Assert.Equal(
            expected,
            PostgreSqlTenantCollationRules.IsSupportedDeterminism(isDeterministic));
    }

    [Theory]
    [InlineData("text", -1, true)]
    [InlineData("varchar", -1, true)]
    [InlineData("varchar", 132, true)]
    [InlineData("varchar", 131, false)]
    [InlineData("varchar", 68, false)]
    [InlineData("bpchar", -1, false)]
    public void TenantColumnCapacityMustCoverTheMaximumCanonicalIdentifier(
        string typeName,
        int typeModifier,
        bool expected)
    {
        Assert.Equal(
            expected,
            PostgreSqlTenantCollationRules.IsSupportedCapacity(typeName, typeModifier));
    }
}
