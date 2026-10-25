using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Repositories;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class TenantSecurityHardeningTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" tenant")]
    [InlineData("tenant ")]
    [InlineData("\ttenant")]
    [InlineData("tenant\n")]
    [InlineData("\u00A0tenant")]
    [InlineData("tenant\u00A0")]
    public void StoreRejectsNonCanonicalStringTenants(string tenantId)
    {
        var store = new TenantScopeStore();

        Assert.False(store.TrySet(tenantId, TenantOperation.Read));
        Assert.False(store.HasTenant);
    }

    [Fact]
    public void StoreRejectsOverlongStringTenant()
    {
        var store = new TenantScopeStore();

        Assert.False(
            store.TrySet(
                new string('a', TenantIdValidation.MaxStringLength + 1),
                TenantOperation.Read));
        Assert.False(store.HasTenant);
    }

    [Fact]
    public void StoreAcceptsCanonicalStringAndGuidTenants()
    {
        var store = new TenantScopeStore();
        string max = new('a', TenantIdValidation.MaxStringLength);

        Assert.True(store.TrySet("tenant-a", TenantOperation.Read));
        Assert.Equal("tenant-a", store.TenantId);
        store.Clear();
        Assert.True(store.TrySet("Acme Corp", TenantOperation.Read));
        store.Clear();
        Assert.True(store.TrySet(max, TenantOperation.Read));

        store.Clear();
        Guid guid = Guid.NewGuid();
        Assert.True(store.TrySet(guid, TenantOperation.Read));
        Assert.Equal(guid, store.TenantId);
    }

    [Fact]
    public async Task ResolverRejectsNonCanonicalStringTenant()
    {
        var store = new TenantScopeStore();
        var coordinator = new TenantScopeCoordinator<StringTenantEntity, string>(
            new StringTenantResolver(" tenant-a "),
            store);
        bool queried = false;

        DataResult<int> result = await coordinator.ExecuteAsync(
            TenantOperation.Read,
            _ =>
            {
                queried = true;
                return ValueTask.FromResult(1);
            });

        Assert.True(result.IsFailure);
        Assert.IsType<ValidationFailure>(result.Failure);
        Assert.False(queried);
        Assert.False(store.HasTenant);
    }

    [Fact]
    public void FakeTenantIdTypeFailsContextConstruction()
    {
        var mapping = new ConfigurableStringTenantMapping
        {
            TenantIdType = typeof(Guid),
        };

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => new FakeTypeStringTenantDbContext(
                    InMemoryOptions<FakeTypeStringTenantDbContext>(),
                    new TenantScopeStore(),
                    [mapping],
                    []));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongTenantPropertyNameFailsContextConstruction()
    {
        var mapping = new ConfigurableStringTenantMapping
        {
            TenantPropertyName = "MissingTenantProperty",
        };

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => new WrongPropertyStringTenantDbContext(
                    InMemoryOptions<WrongPropertyStringTenantDbContext>(),
                    new TenantScopeStore(),
                    [mapping],
                    []));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstantTrueBuildFilterFailsModelValidation()
    {
        using var context = new FakeTypeStringTenantDbContext(
            InMemoryOptions<FakeTypeStringTenantDbContext>(),
            new TenantScopeStore(),
            [new ConstantTrueStringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "contains a predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFilterUsingAnotherPropertyFailsModelValidation()
    {
        using var context = new FakeTypeStringTenantDbContext(
            InMemoryOptions<FakeTypeStringTenantDbContext>(),
            new TenantScopeStore(),
            [new OtherPropertyStringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "contains a predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFilterWithAdditionalPredicateFailsModelValidation()
    {
        using var context = new FakeTypeStringTenantDbContext(
            InMemoryOptions<FakeTypeStringTenantDbContext>(),
            new TenantScopeStore(),
            [new OtherPropertyCanonicalStringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "contains a predicate that is not the direct",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MappingToNonContractPropertyFailsContextConstruction()
    {
        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => new FakeTypeStringTenantDbContext(
                    InMemoryOptions<FakeTypeStringTenantDbContext>(),
                    new TenantScopeStore(),
                    [new NamePropertyStringTenantMapping()],
                    []));

        Assert.Contains(
            "contract property",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValueConverterTenantPropertyFailsModelValidation()
    {
        using var context = new ConverterStringTenantDbContext(
            InMemoryOptions<ConverterStringTenantDbContext>(),
            new TenantScopeStore(),
            [StringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "value converter",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputedTenantPropertyFailsModelValidation()
    {
        using var context = new ComputedStringTenantDbContext(
            InMemoryOptions<ComputedStringTenantDbContext>(),
            new TenantScopeStore(),
            [StringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "computed column",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewMappedTenantEntityFailsModelValidation()
    {
        using var context = new ViewStringTenantDbContext(
            InMemoryOptions<ViewStringTenantDbContext>(),
            new TenantScopeStore(),
            [StringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(() => _ = context.Model);

        Assert.Contains(
            "table rather than a view",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappingGetTenantIdMismatchFailsSaveChanges()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var mapping = new ConfigurableStringTenantMapping
        {
            TenantIdSelector = _ => "different-tenant",
        };
        await using var context = new MismatchedGetTenantIdStringTenantDbContext(
            database.Options<MismatchedGetTenantIdStringTenantDbContext>(),
            store,
            [mapping],
            []);
        await context.Database.EnsureCreatedAsync();

        store.Clear();
        Assert.True(store.TrySet("tenant-a", TenantOperation.Create));
        store.AuthorizeWriteForTesting("tenant-a", TenantOperation.Create);
        context.Items.Add(NewStringItem("tenant-a", "mismatch"));

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<TenantConfigurationFailure>(exception.Failure);
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task DirectSaveChangesRejectsNonCanonicalEntityTenant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = new StringTenantDbContext(
            database.Options<StringTenantDbContext>(),
            store,
            [StringTenantMapping()],
            []);
        await context.Database.EnsureCreatedAsync();

        string[] invalidTenants =
        [
            " tenant-a",
            "tenant-a ",
            new string('a', TenantIdValidation.MaxStringLength + 1),
        ];

        foreach (string invalidTenant in invalidTenants)
        {
            context.ChangeTracker.Clear();
            store.Clear();
            Assert.True(store.TrySet("tenant-a", TenantOperation.Create));
            store.AuthorizeWriteForTesting("tenant-a", TenantOperation.Create);
            context.Items.Add(NewStringItem(invalidTenant, "invalid"));

            SaveChangesSecurityException exception =
                await Assert.ThrowsAsync<SaveChangesSecurityException>(
                    () => context.SaveChangesAsync());

            Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        }

        context.ChangeTracker.Clear();
        store.Clear();
        Assert.True(store.TrySet("tenant-a", TenantOperation.Read));
        context.AuthorizeReadForTesting("tenant-a");
        Assert.Empty(await context.Items.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DirectSaveChangesRejectsNonCanonicalDatabaseTenant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = new StringTenantDbContext(
            database.Options<StringTenantDbContext>(),
            store,
            [StringTenantMapping()],
            []);
        await context.Database.EnsureCreatedAsync();

        var item = NewStringItem("tenant-a", "original");
        store.Clear();
        Assert.True(store.TrySet("tenant-a", TenantOperation.Create));
        store.AuthorizeWriteForTesting("tenant-a", TenantOperation.Create);
        context.Items.Add(item);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE Items SET TenantId = {0} WHERE Id = {1}",
            "tenant-a ",
            item.Id);

        var modified = NewStringItem("tenant-a", "modified");
        modified.Id = item.Id;
        context.Attach(modified);
        context.Entry(modified).State = EntityState.Modified;
        store.Clear();
        Assert.True(store.TrySet("tenant-a", TenantOperation.Update));
        store.AuthorizeWriteForTesting("tenant-a", TenantOperation.Update);

        SaveChangesSecurityException exception =
            await Assert.ThrowsAsync<SaveChangesSecurityException>(
                () => context.SaveChangesAsync());

        Assert.IsType<TenantOwnershipFailure>(exception.Failure);
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task ReadRepositoryDoesNotReturnNonCanonicalStoredTenant()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        var mapping = new CanonicalStringTenantMapping();
        await using var context = new PermissiveStringTenantDbContext(
            database.Options<PermissiveStringTenantDbContext>(),
            store,
            [mapping],
            []);
        await context.Database.EnsureCreatedAsync();

        var item = NewStringItem("tenant-a", "original");
        store.Clear();
        Assert.True(store.TrySet("tenant-a", TenantOperation.Create));
        store.AuthorizeWriteForTesting("tenant-a", TenantOperation.Create);
        context.Items.Add(item);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE Items SET TenantId = {0} WHERE Id = {1}",
            "tenant-a ",
            item.Id);

        var coordinator = new TenantScopeCoordinator<StringTenantEntity, string>(
            new StringTenantResolver("tenant-a"),
            store);
        var repository =
            new EfReadRepository<
                PermissiveStringTenantDbContext,
                StringTenantEntity,
                Guid,
                string>(
                context,
                coordinator,
                entity => entity.Id);

        DataResult<IReadOnlyList<StringTenantEntity>> result =
            await repository.ListAsync(new AllStringSpecification());

        Assert.True(result.IsSuccess, result.Failure?.ToString());
        Assert.Empty(result.Value);
        Assert.Equal(
            1,
            await context.Items.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task ValidateTenantSecuritySucceedsWithoutTenantScope()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new TenantScopeStore();
        await using var context = new StringTenantDbContext(
            database.Options<StringTenantDbContext>(),
            store,
            [StringTenantMapping()],
            []);
        await context.Database.EnsureCreatedAsync();

        Assert.False(store.HasTenant);
        context.ValidateTenantSecurity();
        Assert.False(store.HasTenant);
    }

    [Fact]
    public void ValidateTenantSecurityForGuidModelSucceedsWithoutDatabase()
    {
        using var context = new TestDbContext(
            InMemoryOptions<TestDbContext>(),
            new TenantScopeStore(),
            [TestContexts.OrderTenantMapping(), TestContexts.OrderLineTenantMapping()],
            []);

        context.ValidateTenantSecurity();
    }

    [Fact]
    public void ValidateTenantSecurityFailsWhenVerifierRejects()
    {
        using var context = new RejectingStringTenantDbContext(
            InMemoryOptions<RejectingStringTenantDbContext>(),
            new TenantScopeStore(),
            [StringTenantMapping()],
            []);

        TenantConfigurationException exception =
            Assert.Throws<TenantConfigurationException>(
                () => context.ValidateTenantSecurity());

        Assert.Contains(
            "forced verifier failure",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantIdGetterRejectsNonCanonicalScope()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new StringTenantDbContext(
            database.Options<StringTenantDbContext>(),
            new NonCanonicalTenantScopeStore(" tenant-a"),
            [StringTenantMapping()],
            []);
        await context.Database.EnsureCreatedAsync();

        await Assert.ThrowsAsync<TenantConfigurationException>(
            () => context.Items.AsNoTracking().ToListAsync());
    }

    private static TenantEntityMapping<StringTenantEntity, string> StringTenantMapping()
    {
        return new TenantEntityMapping<StringTenantEntity, string>(
            entity => entity.TenantId);
    }

    private static DbContextOptions<TContext> InMemoryOptions<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
    }

    private static StringTenantEntity NewStringItem(string tenantId, string name)
    {
        return new StringTenantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
        };
    }

    private sealed class AllStringSpecification : ISpecification<StringTenantEntity>
    {
        public Expression<Func<StringTenantEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<StringTenantEntity>> Sorts => [];
    }

    private sealed class ConfigurableStringTenantMapping : ITenantEntityMapping
    {
        private readonly TenantEntityMapping<StringTenantEntity, string> _inner =
            new(entity => entity.TenantId);

        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType { get; init; } = typeof(string);

        public string TenantPropertyName { get; init; } =
            nameof(StringTenantEntity.TenantId);

        public Func<StringTenantEntity, object?>? TenantIdSelector { get; init; }

        public object? GetTenantId(object entity)
        {
            return TenantIdSelector is null
                ? _inner.GetTenantId(entity)
                : TenantIdSelector((StringTenantEntity)entity);
        }

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => _inner.BuildFilter(context);
    }

    private sealed class CanonicalStringTenantMapping : ITenantEntityMapping
    {
        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(StringTenantEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((StringTenantEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => new TenantEntityMapping<StringTenantEntity, string>(
                entity => entity.TenantId).BuildFilter(context);
    }

    private sealed class ConstantTrueStringTenantMapping
        : ITenantEntityMapping, ITenantExactMapping
    {
        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(StringTenantEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((StringTenantEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
        {
            ParameterExpression parameter =
                Expression.Parameter(typeof(StringTenantEntity), "entity");
            return Expression.Lambda(Expression.Constant(true), parameter);
        }
    }

    private sealed class OtherPropertyStringTenantMapping : ITenantEntityMapping
    {
        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(StringTenantEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((StringTenantEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
        {
            ParameterExpression parameter =
                Expression.Parameter(typeof(StringTenantEntity), "entity");
            Expression otherProperty = Expression.Property(
                parameter,
                nameof(StringTenantEntity.Name));
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

    private sealed class OtherPropertyCanonicalStringTenantMapping : ITenantEntityMapping
    {
        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(StringTenantEntity.TenantId);

        public object? GetTenantId(object entity)
            => ((StringTenantEntity)entity).TenantId;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
        {
            LambdaExpression canonical = new TenantEntityMapping<StringTenantEntity, string>(
                entity => entity.TenantId).BuildFilter(context);
            ParameterExpression parameter = canonical.Parameters[0];
            Expression otherProperty = Expression.Property(
                parameter,
                nameof(StringTenantEntity.Name));
            return Expression.Lambda(
                Expression.AndAlso(
                    canonical.Body,
                    Expression.NotEqual(
                        otherProperty,
                        Expression.Constant(null, typeof(string)))),
                parameter);
        }
    }

    private sealed class NamePropertyStringTenantMapping : ITenantEntityMapping
    {
        public Type EntityType => typeof(StringTenantEntity);

        public Type TenantIdType => typeof(string);

        public string TenantPropertyName => nameof(StringTenantEntity.Name);

        public object? GetTenantId(object entity)
            => ((StringTenantEntity)entity).Name;

        public LambdaExpression BuildFilter(MecesoftDbContext context)
            => new TenantEntityMapping<StringTenantEntity, string>(
                entity => entity.Name).BuildFilter(context);
    }

    private sealed class StringTenantResolver(string tenantId)
        : ITenantAccessResolver<StringTenantEntity, string>
    {
        public Expression<Func<StringTenantEntity, string>> TenantIdSelector
            => entity => entity.TenantId;

        public ValueTask<DataResult<TenantScope<string>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<string>(tenantId, operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            StringTenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(
                    string.Equals(root.TenantId, tenantId, StringComparison.Ordinal)));
        }
    }

    private sealed class NonCanonicalTenantScopeStore(string tenantId)
        : ITenantScopeStore
    {
        public object? TenantId => tenantId;

        public TenantOperation? Operation => TenantOperation.Read;

        public bool HasTenant => true;

        public bool TrySet(object scopedTenantId, TenantOperation operation) => true;

        public void Clear()
        {
        }
    }

    private abstract class CustomMappingStringTenantContextBase : MecesoftDbContext
    {
        protected CustomMappingStringTenantContextBase(
            DbContextOptions options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => SqliteStringTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigureStringTenantEntity(modelBuilder);
        }
    }

    private sealed class FakeTypeStringTenantDbContext
        : CustomMappingStringTenantContextBase
    {
        public FakeTypeStringTenantDbContext(
            DbContextOptions<FakeTypeStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class WrongPropertyStringTenantDbContext
        : CustomMappingStringTenantContextBase
    {
        public WrongPropertyStringTenantDbContext(
            DbContextOptions<WrongPropertyStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class MismatchedGetTenantIdStringTenantDbContext
        : CustomMappingStringTenantContextBase
    {
        public MismatchedGetTenantIdStringTenantDbContext(
            DbContextOptions<MismatchedGetTenantIdStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class PermissiveStringTenantDbContext
        : CustomMappingStringTenantContextBase
    {
        public PermissiveStringTenantDbContext(
            DbContextOptions<PermissiveStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }
    }

    private sealed class RejectingStringTenantDbContext : MecesoftDbContext
    {
        public RejectingStringTenantDbContext(
            DbContextOptions<RejectingStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => RejectingTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigureStringTenantEntity(modelBuilder);
        }
    }

    private sealed class ConverterStringTenantDbContext : MecesoftDbContext
    {
        public ConverterStringTenantDbContext(
            DbContextOptions<ConverterStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => SqliteStringTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigureStringTenantEntity(
                modelBuilder,
                tenant => tenant.HasConversion(
                    value => value.ToUpperInvariant(),
                    value => value));
        }
    }

    private sealed class ComputedStringTenantDbContext : MecesoftDbContext
    {
        public ComputedStringTenantDbContext(
            DbContextOptions<ComputedStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => SqliteStringTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            ConfigureStringTenantEntity(
                modelBuilder,
                tenant => tenant.HasComputedColumnSql("'tenant'"));
        }
    }

    private sealed class ViewStringTenantDbContext : MecesoftDbContext
    {
        public ViewStringTenantDbContext(
            DbContextOptions<ViewStringTenantDbContext> options,
            ITenantScopeStore tenantScope,
            IEnumerable<ITenantEntityMapping> tenantMappings,
            IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
            : base(options, tenantScope, tenantMappings, softDeleteMappings)
        {
        }

        public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

        protected override ITenantCollationVerifier? TenantCollationVerifier
            => SqliteStringTenantCollationVerifier.Instance;

        protected override void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StringTenantEntity>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.ToView("StringTenantView");
                entity.Property(item => item.TenantId).IsRequired();
                entity.Property(item => item.Name).IsRequired();
            });
        }
    }

    private static void ConfigureStringTenantEntity(
        ModelBuilder modelBuilder,
        Action<Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<string>>?
            configureTenant = null)
    {
        modelBuilder.Entity<StringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            var tenant = entity.Property(item => item.TenantId).IsRequired();
            configureTenant?.Invoke(tenant);
            entity.Property(item => item.Name).IsRequired();
        });
    }

    private sealed class RejectingTenantCollationVerifier : ITenantCollationVerifier
    {
        public static RejectingTenantCollationVerifier Instance { get; } = new();

        public DataResult<bool> Verify(
            MecesoftDbContext context,
            ITenantEntityMapping mapping)
        {
            return DataResult.Failed<bool>(
                new TenantConfigurationFailure("forced verifier failure"));
        }
    }
}
