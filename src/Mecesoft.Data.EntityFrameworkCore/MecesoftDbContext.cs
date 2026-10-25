using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Concurrency;
using Mecesoft.Data.EntityFrameworkCore.Expressions;
using Mecesoft.Data.EntityFrameworkCore.Modeling;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore;

/// <summary>
/// Base DbContext that installs mandatory named tenant and soft-delete query filters.
/// </summary>
/// <remarks>
/// <para>
/// Derived contexts must configure their entities in <see cref="ConfigureModel"/> rather than
/// overriding <c>OnModelCreating</c>. The foundation seals model creation so filters cannot be
/// skipped accidentally.
/// </para>
/// <para>
/// Every entity implementing <see cref="ITenantEntity{TTenantId}"/> must have exactly one
/// <see cref="ITenantEntityMapping"/> registration. A missing or duplicate mapping fails model
/// creation with <see cref="TenantConfigurationException"/>; it is never silently accepted.
/// </para>
/// <para>
/// All entities implementing <see cref="ISoftDeletable"/> receive a named soft-delete filter. The
/// filter key is stable and can be removed selectively with
/// <see cref="Queries.TenantQueryableExtensions.IgnoreSoftDelete{TEntity}"/>; there is intentionally
/// no helper that removes the tenant filter.
/// </para>
/// <para>
/// Soft-delete state changes are rejected unless they are the exact transition authorized by the
/// secure soft-delete repository after tenant, resource-authorization, and concurrency checks.
/// Physical deletes of <see cref="ISoftDeletable"/> entities are rejected. Soft-deletable dependents
/// of non-soft-deletable principals cannot use cascade delete.
/// </para>
/// <para>
/// Explicitly global entities are configured with
/// <see cref="Modeling.GlobalEntityModelBuilderExtensions.IsGlobal{TEntity}"/> and are never
/// tenant-filtered. A global entity may not implement a tenant contract, have a tenant mapping, or
/// reference a tenant-owned entity.
/// </para>
/// <para>
/// Modified and deleted tenant entities are checked against the database row tenant before
/// persistence. This is not a database-level security boundary; raw SQL commands, bulk
/// update/delete commands, external connections, directly disabled query filters, and
/// database-side access bypass these checks.
/// </para>
/// <para>
/// Direct <c>SaveChanges</c> writes of tenant entities require a write authorization issued by
/// <c>TenantScopeCoordinator</c> after resolver approval.
/// Calling <see cref="Tenancy.ITenantScopeStore.TrySet"/> alone is not sufficient to authorize a
/// write. The authorization is consumed by the next SaveChanges call.
/// Owned entities of a tenant-owned root require the matching owner-scoped
/// Create/Update/HardDelete authorization; ownership cascade deletes performed as part of an
/// authorized owner delete use the owner's HardDelete authorization.
/// </para>
/// <para>
/// Global entities and global-owned entities are denied by default. An explicit,
/// context-bound global write grant issued by
/// <see cref="Authorization.GlobalWriteCoordinator"/> is required for global Add/Update/Delete.
/// Global reads are not tenant-filtered and do not require a tenant read grant.
/// </para>
/// </remarks>
public abstract class MecesoftDbContext : DbContext
{
    private readonly ITenantScopeStore _tenantScope;
    private readonly ITenantEntityMapping[] _tenantMappings;
    private readonly ISoftDeleteEntityMapping[] _softDeleteMappings;
    private readonly Dictionary<Type, ITenantEntityMapping> _tenantMappingsByType;
    private readonly ITenantFilterExpressionAugmenter? _tenantFilterAugmenter;
    private readonly ITenantCollationVerifier? _optionsTenantCollationVerifier;
    private readonly bool _requireTrustedTenantVerifier;
    private readonly bool _requireExactTenantFilters;
    private readonly Dictionary<Type, ConcurrencyTokenAccessor?> _concurrencyAccessors = [];
    private HashSet<Type>? _tenantRelatedTypes;
    private HashSet<Type> _globalEntityTypes = [];
    private SoftDeleteTransition? _pendingSoftDeleteTransition;
    private bool _tenantScopeFrozen;
    private object? _frozenTenantId;
    private TenantOperation? _frozenTenantOperation;
    private bool _stringTenantCollationVerified;
    private bool _tenantTableMappingsValidated;
    private bool _modelMappingsValidated;
    private bool _tenantRelationshipsVerified;
    private DataFailure? _stringTenantCollationFailure;
    private DataFailure? _tenantRelationshipFailure;
    private bool _readAuthorized;
    private object? _authorizedReadTenantId;
    private int _internalFilterDepth;
    private object? _internalFilterTenantId;
    private TenantOperation? _internalFilterOperation;
    private readonly Dictionary<object, TenantOperation> _globalWriteAuthorizations =
        new(ReferenceEqualityComparer.Instance);
    private readonly string _modelCacheKeySignature;
    private string? _globalModelCacheKeySignature;

    private static readonly string CanonicalTenantRule =
        "String tenant identifiers must be non-empty, contain at most "
        + $"{TenantIdValidation.MaxStringLength} UTF-16 code units, and must not start or end "
        + "with whitespace.";

    /// <summary>
    /// Initializes a new foundation context.
    /// </summary>
    /// <param name="options">The DbContext options.</param>
    /// <param name="tenantScope">The scoped tenant scope store.</param>
    /// <param name="tenantMappings">The registered tenant mappings.</param>
    /// <param name="softDeleteMappings">The optional explicit soft-delete mappings.</param>
    protected MecesoftDbContext(
        DbContextOptions options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping>? tenantMappings = null,
        IEnumerable<ISoftDeleteEntityMapping>? softDeleteMappings = null)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantScope);

        CoreOptionsExtension? coreOptions =
            options.FindExtension<CoreOptionsExtension>();
        if (coreOptions?.Model is not null)
        {
            throw new TenantConfigurationException(
                "Externally supplied or compiled EF models are not supported by "
                + "MecesoftDbContext. The tenant and soft-delete filters must be installed by "
                + "the context model pipeline.");
        }

        _tenantScope = tenantScope;
        _tenantMappings = tenantMappings?
            .Select(ValidatedTenantEntityMappingBase.Create)
            .ToArray() ?? [];
        _softDeleteMappings = softDeleteMappings?
            .Select(ValidatedSoftDeleteEntityMapping.Create)
            .ToArray() ?? [];
        _tenantMappingsByType = _tenantMappings
            .GroupBy(mapping => mapping.EntityType)
            .ToDictionary(group => group.Key, group => group.First());
        _modelCacheKeySignature = BuildModelCacheKeySignature(
            _tenantMappings,
            _softDeleteMappings);
        _tenantFilterAugmenter = options.Extensions
            .OfType<TenantFilterAugmentationOptionsExtension>()
            .Select(extension => extension.Augmenter)
            .LastOrDefault();
        TenantFilterAugmentationOptionsExtension? securityExtension = options.Extensions
            .OfType<TenantFilterAugmentationOptionsExtension>()
            .LastOrDefault();
        _optionsTenantCollationVerifier = securityExtension?.Verifier;
        _requireTrustedTenantVerifier =
            securityExtension?.RequireTrustedVerifier ?? false;
        _requireExactTenantFilters =
            securityExtension?.RequireExactTenantFilters ?? false;
    }

    /// <inheritdoc />
    protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, MecesoftModelCacheKeyFactory>();
    }

    internal string GetModelCacheKeySignature()
    {
        _globalModelCacheKeySignature ??= BuildGlobalModelCacheKeySignature();
        return _modelCacheKeySignature + "|global:" + _globalModelCacheKeySignature;
    }

    private string BuildGlobalModelCacheKeySignature()
    {
        try
        {
            // ConfigureModel is the source of global annotations. Build a lightweight probe so
            // contexts of the same type with different global declarations cannot share a model.
            var probeBuilder = new ModelBuilder();
            ConfigureModel(probeBuilder);

            return string.Join(
                "|",
                probeBuilder.Model
                    .GetEntityTypes()
                    .Where(entityType =>
                        entityType.ClrType is not null
                        && entityType.FindAnnotation(
                            GlobalEntityModelBuilderExtensions.AnnotationName)?.Value is true)
                    .Select(entityType => TypeName(entityType.ClrType!))
                    .OrderBy(name => name, StringComparer.Ordinal));
        }
        catch (Exception)
        {
            // A probe failure must not allow model reuse. A unique signature forces a fresh,
            // fully validated model for this context instance.
            return "probe-failed-" + Guid.NewGuid().ToString("N");
        }
    }

    private static string BuildModelCacheKeySignature(
        ITenantEntityMapping[] tenantMappings,
        ISoftDeleteEntityMapping[] softDeleteMappings)
    {
        var builder = new StringBuilder("mecesoft-model-v1");

        foreach (ITenantEntityMapping mapping in tenantMappings
            .OrderBy(item => TypeName(item.EntityType), StringComparer.Ordinal)
            .ThenBy(item => item.TenantPropertyName, StringComparer.Ordinal)
            .ThenBy(item => TypeName(item.TenantIdType), StringComparer.Ordinal)
            .ThenBy(item => TypeName(item.GetType()), StringComparer.Ordinal))
        {
            AppendSignaturePart(builder, "tenant");
            AppendSignaturePart(builder, TypeName(mapping.EntityType));
            AppendSignaturePart(builder, mapping.TenantPropertyName);
            AppendSignaturePart(builder, TypeName(mapping.TenantIdType));
            AppendSignaturePart(
                builder,
                mapping is ValidatedTenantEntityMappingBase validated
                    ? TypeName(validated.SourceType)
                    : TypeName(mapping.GetType()));
            AppendSignaturePart(
                builder,
                mapping is ITenantExactMapping ? "exact" : "plain");
        }

        foreach (ISoftDeleteEntityMapping mapping in softDeleteMappings
            .OrderBy(item => TypeName(item.EntityType), StringComparer.Ordinal)
            .ThenBy(item => TypeName(item.GetType()), StringComparer.Ordinal))
        {
            if (mapping is not ValidatedSoftDeleteEntityMapping validated)
            {
                throw new TenantConfigurationException(
                    "A soft-delete mapping was not validated before model cache key creation.");
            }

            AppendSignaturePart(builder, "soft-delete");
            AppendSignaturePart(builder, TypeName(mapping.EntityType));
            AppendSignaturePart(builder, TypeName(validated.SourceType));
            AppendSignaturePart(builder, validated.Fingerprint);
        }

        return builder.ToString();
    }

    private static void AppendSignaturePart(StringBuilder builder, string value)
    {
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static string TypeName(Type type)
    {
        return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
    }

    /// <summary>
    /// Gets the EF model after validating that current mappings and named filters match it.
    /// </summary>
    /// <remarks>
    /// This also covers a model supplied through <c>UseModel</c> or a compiled model: a model that
    /// lacks the required tenant or soft-delete filters fails closed before a query executes.
    /// </remarks>
    public sealed override IModel Model
    {
        get
        {
            IModel model = base.Model;
            _globalEntityTypes = BuildGlobalEntityTypes(model);
            EnsureModelMappingsValidated(model);
            return model;
        }
    }

    /// <summary>
    /// Gets the current scoped tenant identifier, or <see langword="null"/> when none was resolved.
    /// </summary>
    public object? TenantId
    {
        get
        {
            EnsureTenantCollationVerified();
            object? tenantId = _tenantScopeFrozen
                ? _frozenTenantId
                : _tenantScope.TenantId;
            TenantOperation? currentOperation = _tenantScopeFrozen
                ? _frozenTenantOperation
                : _tenantScope.Operation;
            if (tenantId is string text && !TenantIdValidation.IsCanonical(text))
            {
                throw new TenantConfigurationException(
                    $"The current tenant identifier is not canonical. {CanonicalTenantRule}");
            }

            bool readAuthorized = _readAuthorized
                && _authorizedReadTenantId is not null
                && ConcurrencyTokenAccessor.ValuesEqual(
                    _authorizedReadTenantId,
                    tenantId);
            bool internalFilterAuthorized = _internalFilterDepth > 0
                && _internalFilterTenantId is not null
                && _internalFilterOperation == currentOperation
                && ConcurrencyTokenAccessor.ValuesEqual(
                    _internalFilterTenantId,
                    tenantId);

            if (tenantId is null
                || (!readAuthorized && !internalFilterAuthorized))
            {
                throw new TenantConfigurationException(
                    "A resolver-issued read scope is required before querying tenant data.");
            }

            return tenantId;
        }
    }

    /// <summary>
    /// Authorizes read access for a resolver-issued scope on this context instance.
    /// </summary>
    /// <param name="tenantId">The resolved tenant identifier.</param>
    /// <param name="operation">The resolver operation that authorized the scope.</param>
    /// <exception cref="TenantConfigurationException">
    /// The scope does not match the current store state or cannot be authorized.
    /// </exception>
    internal void AuthorizeReadScope(
        object tenantId,
        TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (operation != Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Read)
        {
            throw new TenantConfigurationException(
                "Only a Read operation can authorize a general read scope.");
        }

        AuthorizeReadCore(tenantId, operation);
    }

    internal void AuthorizeReadForTesting(object tenantId)
    {
        AuthorizeReadCore(
            tenantId,
            requiredOperation: null);
    }

    private void AuthorizeReadCore(
        object tenantId,
        TenantOperation? requiredOperation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (tenantId is string text && !TenantIdValidation.IsCanonical(text))
        {
            throw new TenantConfigurationException(
                $"The resolved tenant identifier is not canonical. {CanonicalTenantRule}");
        }

        object? currentTenantId = _tenantScopeFrozen
            ? _frozenTenantId
            : _tenantScope.TenantId;
        TenantOperation? currentOperation = _tenantScopeFrozen
            ? _frozenTenantOperation
            : _tenantScope.Operation;

        if (currentTenantId is null
            || (requiredOperation is not null
                && currentOperation != requiredOperation)
            || !ConcurrencyTokenAccessor.ValuesEqual(currentTenantId, tenantId))
        {
            throw new TenantConfigurationException(
                "The read scope does not match the current resolver-issued tenant scope.");
        }

        _authorizedReadTenantId = tenantId;
        _readAuthorized = true;
    }

    internal void ClearReadAuthorization()
    {
        _readAuthorized = false;
        _authorizedReadTenantId = null;
    }

    internal IDisposable BeginInternalTenantFilterScope(
        object tenantId,
        TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (operation == Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Read)
        {
            throw new TenantConfigurationException(
                "Read operations must use a resolver-issued read grant.");
        }

        if (tenantId is string text && !TenantIdValidation.IsCanonical(text))
        {
            throw new TenantConfigurationException(
                $"The current tenant identifier is not canonical. {CanonicalTenantRule}");
        }

        object? currentTenantId = _tenantScopeFrozen
            ? _frozenTenantId
            : _tenantScope.TenantId;
        TenantOperation? currentOperation = _tenantScopeFrozen
            ? _frozenTenantOperation
            : _tenantScope.Operation;

        if (currentTenantId is null
            || currentOperation != operation
            || !ConcurrencyTokenAccessor.ValuesEqual(currentTenantId, tenantId))
        {
            throw new TenantConfigurationException(
                "The internal tenant filter scope does not match the current tenant scope.");
        }

        if (_internalFilterDepth > 0
            && (_internalFilterOperation != operation
                || !ConcurrencyTokenAccessor.ValuesEqual(
                    _internalFilterTenantId,
                    tenantId)))
        {
            throw new TenantConfigurationException(
                "Nested tenant filter scopes must use the same tenant and operation.");
        }

        if (_internalFilterDepth == 0)
        {
            _internalFilterTenantId = tenantId;
            _internalFilterOperation = operation;
        }

        _internalFilterDepth++;
        return new InternalTenantFilterScope(this);
    }

    internal void ClearAuthorizationGrants()
    {
        ClearReadAuthorization();
        ClearTenantWriteAuthorization();
        _globalWriteAuthorizations.Clear();
        _internalFilterDepth = 0;
        _internalFilterTenantId = null;
        _internalFilterOperation = null;
    }

    private sealed class InternalTenantFilterScope : IDisposable
    {
        private MecesoftDbContext? _context;

        public InternalTenantFilterScope(MecesoftDbContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            MecesoftDbContext? context = _context;
            if (context is null)
            {
                return;
            }

            _context = null;
            context._internalFilterDepth--;
            if (context._internalFilterDepth <= 0)
            {
                context._internalFilterDepth = 0;
                context._internalFilterTenantId = null;
                context._internalFilterOperation = null;
            }
        }
    }

    internal bool IsReadAuthorizedFor(object tenantId)
    {
        return _readAuthorized
            && _authorizedReadTenantId is not null
            && ConcurrencyTokenAccessor.ValuesEqual(
                _authorizedReadTenantId,
                tenantId);
    }

    /// <summary>
    /// Validates model, tenant mapping, and collation security without requiring a tenant scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this method during application startup after the database schema is available. It
    /// forces model validation, verifies every string tenant mapping with the configured
    /// <see cref="ITenantCollationVerifier"/>, and throws on the first failure.
    /// </para>
    /// <para>
    /// This method validates configuration, not stored tenant data. A successful result does not
    /// mean existing rows contain canonical tenant identifiers. It also does not remove the
    /// adapter's documented raw SQL, bulk, or <c>IgnoreQueryFilters</c> bypasses.
    /// </para>
    /// </remarks>
    /// <exception cref="TenantConfigurationException">
    /// The model, tenant mapping, canonical rules, or collation verification is invalid.
    /// </exception>
    public void ValidateTenantSecurity()
    {
        _ = Model;
        EnsureModelMappingsValidated();

        DataResult<bool> result = VerifyTenantCollation();
        if (result.IsFailure)
        {
            throw new TenantConfigurationException(result.Failure!.Message);
        }
    }

    /// <summary>
    /// Gets the operation for which the current tenant scope was resolved.
    /// </summary>
    public TenantOperation? TenantOperation => _tenantScopeFrozen
        ? _frozenTenantOperation
        : _tenantScope.Operation;

    /// <summary>
    /// Gets the runtime verifier used for string tenant identifiers.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="null"/>, which fails closed for string tenant mappings. Derived
    /// contexts that use string tenant identifiers must override this property with a verifier that
    /// inspects the actual database/column collation at runtime. Guid and other non-string tenant
    /// identifiers do not require a verifier.
    /// </remarks>
    protected virtual ITenantCollationVerifier? TenantCollationVerifier => null;

    /// <summary>
    /// Gets whether a provider exactness augmenter is configured for tenant filters.
    /// </summary>
    /// <remarks>
    /// Provider production verifiers can require this flag before accepting string tenant
    /// mappings. The flag proves configuration presence, not the correctness of the augmenter.
    /// </remarks>
    public bool HasTenantFilterAugmentation => _tenantFilterAugmenter is not null;

    /// <summary>
    /// Gets the configured tenant filter augmenter instance, or <see langword="null"/> when none is
    /// configured.
    /// </summary>
    /// <remarks>
    /// This exposes configuration for trusted provider verifiers; it is not an authorization token.
    /// </remarks>
    internal ITenantFilterExpressionAugmenter? TenantFilterAugmenter =>
        _tenantFilterAugmenter;

    /// <summary>
    /// Gets whether a trusted tenant collation verifier was configured through context options.
    /// </summary>
    /// <remarks>
    /// This flag proves configuration presence, not the verifier's correctness. Provider production
    /// verifiers can require it before accepting string tenant mappings.
    /// </remarks>
    public bool HasTenantCollationVerification =>
        _optionsTenantCollationVerifier is not null;

    private ITenantCollationVerifier? ResolveTenantCollationVerifier()
        => _optionsTenantCollationVerifier ?? TenantCollationVerifier;

    /// <summary>
    /// Configures application entities. Foundation filters are applied after this method returns.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    protected virtual void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    /// <inheritdoc />
    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureModel(modelBuilder);
        _tenantFilterAugmenter?.ConfigureModel(modelBuilder);
        _globalEntityTypes = BuildGlobalEntityTypes(modelBuilder);
        ApplyTenantFilters(modelBuilder);
        ApplySoftDeleteFilters(modelBuilder);
        ValidateMappings(modelBuilder);
    }

    /// <inheritdoc />
    public sealed override int SaveChanges()
        => SaveChanges(acceptAllChangesOnSuccess: true);

    /// <inheritdoc />
    public sealed override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            ValidateTenantCollationOrThrow();
            EnsureModelMappingsValidatedOrThrow();
            ValidatePendingChanges();
            PrepareConcurrencyTokens();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            _pendingSoftDeleteTransition = null;
            ClearTenantWriteAuthorization();
            _globalWriteAuthorizations.Clear();
        }
    }

    /// <summary>
    /// Freezes the tenant scope for the duration of a unit-of-work commit.
    /// </summary>
    internal void FreezeTenantScope()
    {
        if (_tenantScopeFrozen)
        {
            return;
        }

        _frozenTenantId = _tenantScope.TenantId;
        _frozenTenantOperation = _tenantScope.Operation;
        _tenantScopeFrozen = true;
    }

    /// <summary>
    /// Restores the tenant scope captured before the commit started and removes any participant
    /// changes to the shared scope store.
    /// </summary>
    internal void UnfreezeTenantScope()
    {
        if (!_tenantScopeFrozen)
        {
            return;
        }

        object? tenantId = _frozenTenantId;
        TenantOperation? operation = _frozenTenantOperation;
        _tenantScopeFrozen = false;
        _frozenTenantId = null;
        _frozenTenantOperation = null;

        _tenantScope.Clear();
        if (tenantId is not null)
        {
            _ = _tenantScope.TrySet(
                tenantId,
                operation ?? Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Read);
        }
    }

    /// <inheritdoc />
    public sealed override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    /// <inheritdoc />
    public sealed override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateTenantCollationOrThrow();
            EnsureModelMappingsValidatedOrThrow();
            await ValidatePendingChangesAsync(cancellationToken).ConfigureAwait(false);
            PrepareConcurrencyTokens();
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pendingSoftDeleteTransition = null;
            ClearTenantWriteAuthorization();
            _globalWriteAuthorizations.Clear();
        }
    }

    internal ConcurrencyTokenAccessor? GetConcurrencyTokenAccessor(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (_concurrencyAccessors.TryGetValue(entityType, out ConcurrencyTokenAccessor? cached))
        {
            return cached;
        }

        var entityMetadata = Model.FindEntityType(entityType)
            ?? throw new ConcurrencyConfigurationException(
                $"The entity type '{entityType.Name}' is not part of the DbContext model.");

        ConcurrencyTokenAccessor? accessor = ConcurrencyTokenAccessor.Create(entityMetadata);
        _concurrencyAccessors[entityType] = accessor;
        return accessor;
    }

    internal ConcurrencyTokenAccessor GetRequiredConcurrencyTokenAccessor(Type entityType)
    {
        return GetConcurrencyTokenAccessor(entityType)
            ?? throw new ConcurrencyConfigurationException(
                $"The entity type '{entityType.Name}' has no concurrency token.");
    }

    internal DataResult<bool> VerifyTenantCollation()
    {
        if (!_stringTenantCollationVerified)
        {
            if (_stringTenantCollationFailure is not null)
            {
                return DataResult.Failed<bool>(_stringTenantCollationFailure);
            }

            DataResult<bool> tableMappings = ValidateTenantTableMappings();
            if (tableMappings.IsFailure)
            {
                return FailString(tableMappings.Failure!);
            }

            ITenantEntityMapping[] stringMappings = _tenantMappings
                .Where(RequiresTenantCollationVerification)
                .ToArray();

            if (stringMappings.Length > 0)
            {
                ITenantCollationVerifier? verifier = ResolveTenantCollationVerifier();
                if (verifier is null)
                {
                    return FailString(
                        new TenantConfigurationFailure(
                            "String tenant identifiers require a runtime tenant collation verifier."));
                }

                foreach (ITenantEntityMapping mapping in stringMappings)
                {
                    DataResult<bool> result;
                    try
                    {
                        result = verifier.Verify(this, mapping);
                    }
                    catch (Exception)
                    {
                        return FailString(
                            new TenantConfigurationFailure(
                                "The tenant collation verifier failed."));
                    }

                    if (result.IsFailure)
                    {
                        return FailString(result.Failure!);
                    }

                    if (!result.Value)
                    {
                        return FailString(
                            new TenantConfigurationFailure(
                                $"The tenant comparison for '{mapping.EntityType.Name}' is not case-sensitive."));
                    }
                }
            }

            _stringTenantCollationVerified = true;
        }

        return VerifyTenantRelationships();

        DataResult<bool> FailString(DataFailure failure)
        {
            _stringTenantCollationFailure = failure;
            return DataResult.Failed<bool>(failure);
        }
    }

    private DataResult<bool> VerifyTenantRelationships()
    {
        if (_tenantRelationshipsVerified)
        {
            return DataResult.Succeeded(true);
        }

        if (_tenantRelationshipFailure is not null)
        {
            return DataResult.Failed<bool>(_tenantRelationshipFailure);
        }

        ITenantCollationVerifier? verifier = ResolveTenantCollationVerifier();
        if (verifier is not ITenantRelationshipVerifier relationshipVerifier)
        {
            _tenantRelationshipsVerified = true;
            return DataResult.Succeeded(true);
        }

        DataResult<bool> result;
        try
        {
            result = relationshipVerifier.VerifyRelationships(this);
        }
        catch (Exception)
        {
            return FailRelationships(
                new TenantConfigurationFailure(
                    "The tenant relationship verifier failed."));
        }

        if (result.IsFailure)
        {
            return FailRelationships(result.Failure!);
        }

        if (!result.Value)
        {
            return FailRelationships(
                new TenantConfigurationFailure(
                    "The tenant relationship verification did not complete successfully."));
        }

        _tenantRelationshipsVerified = true;
        return DataResult.Succeeded(true);

        DataResult<bool> FailRelationships(DataFailure failure)
        {
            _tenantRelationshipFailure = failure;
            return DataResult.Failed<bool>(failure);
        }
    }

    internal void ValidateTenantCollationOrThrow()
    {
        DataResult<bool> result = VerifyTenantCollation();
        if (result.IsFailure)
        {
            throw new SaveChangesSecurityException(result.Failure!);
        }
    }

    private void EnsureModelMappingsValidatedOrThrow()
    {
        try
        {
            EnsureModelMappingsValidated();
        }
        catch (TenantConfigurationException exception)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(exception.Message));
        }
    }

    private void EnsureModelMappingsValidated()
        => EnsureModelMappingsValidated(base.Model);

    private void EnsureModelMappingsValidated(IModel model)
    {
        if (_modelMappingsValidated)
        {
            return;
        }

        foreach (ITenantEntityMapping mapping in _tenantMappings)
        {
            IEntityType? entityType = model.FindEntityType(mapping.EntityType);
            if (entityType is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' does not match an entity in the model.");
            }

            ValidateTenantPropertyMapping(entityType, mapping);

            LambdaExpression mappingFilter;
            try
            {
                mappingFilter = mapping.BuildFilter(this);
            }
            catch (TenantConfigurationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new TenantConfigurationException(
                    $"The tenant filter for '{mapping.EntityType.Name}' could not be built.");
            }

            ValidateMappingTenantFilter(mapping, mappingFilter);

            if (entityType.FindDeclaredQueryFilter(MecesoftQueryFilterKeys.Tenant) is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant filter is missing for '{mapping.EntityType.Name}'.");
            }
        }

        var explicitSoftDeleteMappings = _softDeleteMappings
            .GroupBy(mapping => mapping.EntityType)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (IEntityType entityType in model.GetEntityTypes())
        {
            Type? clrType = entityType.ClrType;
            if (clrType is null
                || entityType.IsOwned()
                || !typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                continue;
            }

            LambdaExpression expectedFilter =
                explicitSoftDeleteMappings.TryGetValue(
                    clrType,
                    out ISoftDeleteEntityMapping? mapping)
                    ? mapping.BuildFilter()
                    : BuildDefaultSoftDeleteFilter(clrType);

            LambdaExpression? declaredFilter = entityType
                .FindDeclaredQueryFilter(MecesoftQueryFilterKeys.SoftDelete)
                ?.Expression;
            if (declaredFilter is null)
            {
                throw new TenantConfigurationException(
                    $"The soft-delete filter is missing for '{clrType.Name}'.");
            }

            bool matches;
            try
            {
                matches = ExpressionFingerprint.AreEqual(
                    expectedFilter,
                    declaredFilter);
            }
            catch (TenantConfigurationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new TenantConfigurationException(
                    $"The soft-delete filter for '{clrType.Name}' could not be compared.");
            }

            if (!matches)
            {
                throw new TenantConfigurationException(
                    $"The soft-delete filter for '{clrType.Name}' does not match the cached model.");
            }
        }

        foreach (ISoftDeleteEntityMapping mapping in _softDeleteMappings)
        {
            if (model.FindEntityType(mapping.EntityType) is null)
            {
                throw new TenantConfigurationException(
                    $"The soft-delete mapping for '{mapping.EntityType.Name}' does not match an entity in the model.");
            }
        }

        _modelMappingsValidated = true;
    }

    private void EnsureTenantCollationVerified()
    {
        DataResult<bool> result = VerifyTenantCollation();
        if (result.IsFailure)
        {
            throw new TenantConfigurationException(result.Failure!.Message);
        }
    }

    private DataResult<bool> ValidateTenantTableMappings()
    {
        if (_tenantTableMappingsValidated)
        {
            return DataResult.Succeeded(true);
        }

        foreach (ITenantEntityMapping mapping in _tenantMappings)
        {
            IEntityType? entityType = Model.FindEntityType(mapping.EntityType);
            if (entityType is null)
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant mapping for '{mapping.EntityType.Name}' does not match an entity in the model."));
            }

            IProperty? property = entityType.FindProperty(mapping.TenantPropertyName);
            if (property is null)
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant property '{mapping.TenantPropertyName}' on "
                        + $"'{mapping.EntityType.Name}' is not mapped."));
            }

            IColumnMapping[] tableMappings = property
                .GetTableColumnMappings()
                .ToArray();

            if (tableMappings.Length != 1)
            {
                return DataResult.Failed<bool>(
                    new TenantConfigurationFailure(
                        $"The tenant property '{mapping.TenantPropertyName}' on "
                        + $"'{mapping.EntityType.Name}' must map to exactly one table column."));
            }
        }

        _tenantTableMappingsValidated = true;
        return DataResult.Succeeded(true);
    }

    private bool RequiresTenantCollationVerification(ITenantEntityMapping mapping)
    {
        if (mapping.TenantIdType == typeof(string)
            || mapping.TenantIdType == typeof(object))
        {
            return true;
        }

        IProperty? property = Model
            .FindEntityType(mapping.EntityType)
            ?.FindProperty(mapping.TenantPropertyName);

        return property?.ClrType == typeof(string);
    }

    private object? GetScopedTenantIdForSave()
    {
        object? tenantId = _tenantScopeFrozen
            ? _frozenTenantId
            : _tenantScope.TenantId;

        if (tenantId is string text && !TenantIdValidation.IsCanonical(text))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    $"The current tenant identifier is not canonical. {CanonicalTenantRule}"));
        }

        return tenantId;
    }

    private void EnsureTenantWriteAuthorized(
        EntityEntry entry,
        object scopedTenantId)
    {
        Mecesoft.Data.Abstractions.Tenancy.TenantOperation requiredOperation = entry.State switch
        {
            EntityState.Added =>
                Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Create,
            EntityState.Deleted =>
                Mecesoft.Data.Abstractions.Tenancy.TenantOperation.HardDelete,
            EntityState.Modified when _pendingSoftDeleteTransition is not null
                && _pendingSoftDeleteTransition.Matches(entry) =>
                _pendingSoftDeleteTransition.Operation == SoftDeleteOperation.SoftDelete
                    ? Mecesoft.Data.Abstractions.Tenancy.TenantOperation.SoftDelete
                    : Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Restore,
            EntityState.Modified =>
                Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Update,
            _ => Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Update,
        };

        TenantWriteAuthorization? authorization =
            TenantWriteAuthorizationRegistry.Get(_tenantScope);

        if (_tenantScope.Operation != requiredOperation
            || authorization is null
            || authorization.Operation != requiredOperation
            || !TenantWriteAuthorizationRegistry.TryAuthorize(
                _tenantScope,
                this,
                scopedTenantId,
                requiredOperation))
        {
            throw new SaveChangesSecurityException(
                new AuthorizationFailure(
                    "tenant_write",
                    $"A resolver-issued {requiredOperation} authorization is required "
                    + "before persisting tenant-owned entities."));
        }
    }

    private void ClearTenantWriteAuthorization()
    {
        TenantWriteAuthorizationRegistry.ClearForContext(_tenantScope, this);
    }

    internal void AuthorizeGlobalWrite(
        object entity,
        TenantOperation operation)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (operation is not (
            Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Create
            or Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Update
            or Mecesoft.Data.Abstractions.Tenancy.TenantOperation.HardDelete))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Global write authorization supports Create, Update, and HardDelete only.");
        }

        if (_globalWriteAuthorizations.TryGetValue(
            entity,
            out Mecesoft.Data.Abstractions.Tenancy.TenantOperation existing)
            && existing != operation)
        {
            throw new InvalidOperationException(
                "A global entity cannot hold different write authorizations in one unit of work.");
        }

        _globalWriteAuthorizations[entity] = operation;
    }

    private void EnsureGlobalWriteAuthorized(
        object entity,
        Mecesoft.Data.Abstractions.Tenancy.TenantOperation requiredOperation)
    {
        if (entity is null
            || !_globalWriteAuthorizations.TryGetValue(
                entity,
                out Mecesoft.Data.Abstractions.Tenancy.TenantOperation authorizedOperation)
            || authorizedOperation != requiredOperation)
        {
            throw new SaveChangesSecurityException(
                new AuthorizationFailure(
                    "global_write",
                    $"An explicit global {requiredOperation} authorization is required "
                    + "before persisting global entities."));
        }
    }

    private static Mecesoft.Data.Abstractions.Tenancy.TenantOperation
        GetRequiredWriteOperation(EntityEntry entry)
    {
        return entry.State switch
        {
            EntityState.Added =>
                Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Create,
            EntityState.Deleted =>
                Mecesoft.Data.Abstractions.Tenancy.TenantOperation.HardDelete,
            _ => Mecesoft.Data.Abstractions.Tenancy.TenantOperation.Update,
        };
    }

    private static IProperty GetMappedTenantProperty(
        EntityEntry entry,
        ITenantEntityMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.TenantPropertyName))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "Tenant validation requires a direct tenant property mapping."));
        }

        IProperty? property = entry.Metadata.FindProperty(mapping.TenantPropertyName);
        if (property is null)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The tenant property '{mapping.TenantPropertyName}' is not mapped."));
        }

        return property;
    }

    private static void EnsureCanonicalStringTenant(
        object? value,
        Type tenantIdType,
        string source)
    {
        if (!TenantValueGuard.IsCanonical(value, tenantIdType))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    $"{source} is not canonical. {CanonicalTenantRule}"));
        }
    }

    internal Expression<Func<TEntity, bool>> GetSoftDeletePredicate<TEntity>()
        where TEntity : class, ISoftDeletable
    {
        LambdaExpression visibility = GetSoftDeleteVisibilityFilter<TEntity>();

        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "entity");
        var replacer = new ExpressionParameterReplacer(
            visibility.Parameters[0],
            parameter);
        Expression deletedBody = Expression.Not(replacer.Visit(visibility.Body)!);
        return Expression.Lambda<Func<TEntity, bool>>(deletedBody, parameter);
    }

    internal Func<TEntity, bool> GetSoftDeleteVisibility<TEntity>()
        where TEntity : class, ISoftDeletable
    {
        var visibility = (Expression<Func<TEntity, bool>>)
            GetSoftDeleteVisibilityFilter<TEntity>();
        return visibility.Compile();
    }

    private LambdaExpression GetSoftDeleteVisibilityFilter<TEntity>()
        where TEntity : class, ISoftDeletable
    {
        ISoftDeleteEntityMapping? mapping = _softDeleteMappings
            .FirstOrDefault(candidate => candidate.EntityType == typeof(TEntity));
        return mapping?.BuildFilter()
            ?? BuildDefaultSoftDeleteFilter(typeof(TEntity));
    }

    internal void AuthorizeSoftDeleteTransition(
        object entity,
        SoftDeleteOperation operation,
        DateTimeOffset? deletedAtUtc)
    {
        if (_pendingSoftDeleteTransition is not null)
        {
            throw new InvalidOperationException(
                "A soft-delete transition is already pending for this context.");
        }

        _pendingSoftDeleteTransition = new SoftDeleteTransition(
            entity,
            operation,
            deletedAtUtc);
    }

    internal void ClearSoftDeleteTransition()
        => _pendingSoftDeleteTransition = null;

    private void ValidatePendingChanges()
    {
        foreach (EntityEntry entry in GetPendingEntries())
        {
            ValidateEntry(entry);
            if (RequiresDatabaseVerification(entry))
            {
                ValidateDatabaseTenant(entry, GetDatabaseValues(entry));
            }
        }
    }

    private async Task ValidatePendingChangesAsync(CancellationToken cancellationToken)
    {
        foreach (EntityEntry entry in GetPendingEntries())
        {
            ValidateEntry(entry);
            if (RequiresDatabaseVerification(entry))
            {
                PropertyValues? databaseValues = await GetDatabaseValuesAsync(
                    entry,
                    cancellationToken).ConfigureAwait(false);
                ValidateDatabaseTenant(entry, databaseValues);
            }
        }
    }

    private EntityEntry[] GetPendingEntries()
    {
        try
        {
            return ChangeTracker.Entries()
                .Where(entry => entry.State is EntityState.Added
                    or EntityState.Modified
                    or EntityState.Deleted)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "primary_key",
                    "A protected key or relationship key was modified."));
        }
    }

    private void ValidateEntry(EntityEntry entry)
    {
        Type clrType = entry.Metadata.ClrType;
        if (_tenantMappingsByType.TryGetValue(clrType, out ITenantEntityMapping? mapping))
        {
            ValidateTenantEntry(entry, mapping);
        }
        else if (ImplementsTenantEntity(clrType))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The tenant entity '{clrType.Name}' has no tenant mapping."));
        }
        else if (_globalEntityTypes.Contains(clrType))
        {
            EnsureGlobalWriteAuthorized(
                entry.Entity,
                GetRequiredWriteOperation(entry));
        }
        else if (!entry.Metadata.IsOwned()
            && IsTenantRelated(clrType))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The related entity '{clrType.Name}' has no tenant mapping or global declaration."));
        }

        if (entry.State == EntityState.Added
            && entry.Entity is ISoftDeletable addedSoftDeletable
            && (addedSoftDeletable.IsDeleted || addedSoftDeletable.DeletedAtUtc is not null))
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "soft_delete_state",
                    "New entities cannot be inserted in a soft-deleted state."));
        }

        if (entry.State == EntityState.Deleted
            && entry.Entity is ISoftDeletable)
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "soft_delete_state",
                    "Soft-deletable entities cannot be physically deleted."));
        }

        if (entry.State == EntityState.Modified)
        {
            ValidateKeyUnchanged(entry);
            ValidateConcurrencyTokenUnchanged(entry);
        }

        if (entry.State is EntityState.Added or EntityState.Modified
            || (entry.State == EntityState.Deleted && entry.Metadata.IsOwned()))
        {
            ValidateTenantRelationships(entry);
        }

        if (entry.State == EntityState.Modified)
        {
            SoftDeleteTransition? transition = _pendingSoftDeleteTransition;
            if (transition is not null && transition.Matches(entry))
            {
                transition.Validate(entry);
            }
            else
            {
                ValidateSoftDeleteUnchanged(entry);
            }
        }
    }

    private bool RequiresDatabaseVerification(EntityEntry entry)
    {
        if (entry.State is not (EntityState.Modified or EntityState.Deleted))
        {
            return false;
        }

        if (entry.Metadata.IsOwned()
            || !_tenantMappingsByType.ContainsKey(entry.Metadata.ClrType))
        {
            return false;
        }

        return entry.State != EntityState.Deleted
            || entry.Entity is not ISoftDeletable;
    }

    private static PropertyValues? GetDatabaseValues(EntityEntry entry)
    {
        try
        {
            return entry.GetDatabaseValues();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SaveChangesSecurityException(
                new PersistenceFailure(
                    "The database row tenant ownership could not be verified."));
        }
    }

    private static async ValueTask<PropertyValues?> GetDatabaseValuesAsync(
        EntityEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SaveChangesSecurityException(
                new PersistenceFailure(
                    "The database row tenant ownership could not be verified."));
        }
    }

    private void ValidateDatabaseTenant(
        EntityEntry entry,
        PropertyValues? databaseValues)
    {
        if (databaseValues is null)
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The database row tenant ownership could not be verified because "
                    + "the row was not found or was not visible."));
        }

        if (!_tenantMappingsByType.TryGetValue(
            entry.Metadata.ClrType,
            out ITenantEntityMapping? mapping))
        {
            return;
        }

        if (string.IsNullOrEmpty(mapping.TenantPropertyName))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "Database tenant verification requires a direct tenant property mapping."));
        }

        IProperty tenantProperty = GetMappedTenantProperty(entry, mapping);

        object? databaseTenantId;
        try
        {
            databaseTenantId = databaseValues[mapping.TenantPropertyName];
        }
        catch (Exception)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "The database tenant property could not be read for verification."));
        }

        EnsureCanonicalStringTenant(
            databaseTenantId,
            tenantProperty.ClrType,
            "The database row tenant");

        object? scopedTenantId = GetScopedTenantIdForSave();
        if (scopedTenantId is null
            || !ConcurrencyTokenAccessor.ValuesEqual(databaseTenantId, scopedTenantId))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The database row does not belong to the current tenant scope."));
        }

        object? entityTenantId = entry
            .Property(mapping.TenantPropertyName)
            .CurrentValue;
        EnsureCanonicalStringTenant(
            entityTenantId,
            tenantProperty.ClrType,
            "The entity tenant");

        if (!ConcurrencyTokenAccessor.ValuesEqual(entityTenantId, databaseTenantId))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The entity tenant does not match the database row tenant."));
        }
    }

    private void ValidateTenantEntry(EntityEntry entry, ITenantEntityMapping mapping)
    {
        IProperty tenantProperty = GetMappedTenantProperty(entry, mapping);

        object? mappedTenantId = mapping.GetTenantId(entry.Entity);
        object? propertyTenantId = entry
            .Property(mapping.TenantPropertyName)
            .CurrentValue;

        EnsureCanonicalStringTenant(
            mappedTenantId,
            tenantProperty.ClrType,
            "The mapped entity tenant");
        EnsureCanonicalStringTenant(
            propertyTenantId,
            tenantProperty.ClrType,
            "The entity tenant property");

        if (!ConcurrencyTokenAccessor.ValuesEqual(mappedTenantId, propertyTenantId))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "The tenant mapping does not match the mapped entity tenant property."));
        }

        object? scopedTenantId = GetScopedTenantIdForSave();
        if (scopedTenantId is null)
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "A tenant scope is required before persisting tenant-owned entities."));
        }

        EnsureTenantWriteAuthorized(entry, scopedTenantId);

        if (!ConcurrencyTokenAccessor.ValuesEqual(propertyTenantId, scopedTenantId))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The entity tenant does not match the current tenant scope."));
        }

        if (entry.State != EntityState.Modified)
        {
            return;
        }

        var property = entry.Property(mapping.TenantPropertyName);
        if (!ConcurrencyTokenAccessor.ValuesEqual(
            property.OriginalValue,
            property.CurrentValue))
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "tenant_id",
                    "The tenant identifier was modified by the entity mutation."));
        }
    }

    private void ValidateTenantRelationships(EntityEntry entry)
    {
        if (entry.Metadata.IsOwned())
        {
            ValidateOwnedTenantRelationships(entry);
            return;
        }

        if (!_tenantMappingsByType.TryGetValue(
            entry.Metadata.ClrType,
            out ITenantEntityMapping? dependentMapping))
        {
            return;
        }

        foreach (IForeignKey foreignKey in entry.Metadata.GetForeignKeys())
        {
            if (entry.State == EntityState.Modified
                && !ForeignKeyChanged(entry, foreignKey))
            {
                continue;
            }

            if (!foreignKey.IsRequired
                && ForeignKeyIsNull(entry, foreignKey))
            {
                continue;
            }

            Type? principalClrType = foreignKey.PrincipalEntityType.ClrType;
            ITenantEntityMapping? principalMapping = null;
            if (principalClrType is null
                || !_tenantMappingsByType.TryGetValue(
                    principalClrType,
                    out principalMapping))
            {
                if (foreignKey.PrincipalEntityType.IsOwned()
                    || (principalClrType is not null
                        && _globalEntityTypes.Contains(principalClrType)))
                {
                    continue;
                }

                throw new SaveChangesSecurityException(
                    new TenantConfigurationFailure(
                        $"The related principal '{principalClrType?.Name ?? "unknown"}' "
                        + "has no tenant mapping or global declaration."));
            }

            if (principalMapping is null)
            {
                throw new SaveChangesSecurityException(
                    new TenantConfigurationFailure(
                        "The related principal tenant mapping could not be resolved."));
            }

            EntityEntry? principal = FindTrackedPrincipal(entry, foreignKey);
            if (principal is null)
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "A tenant-owned relationship requires the related principal to be tracked and validated."));
            }

            object? dependentTenantId = entry
                .Property(dependentMapping.TenantPropertyName)
                .CurrentValue;
            object? principalTenantId = principal
                .Property(principalMapping.TenantPropertyName)
                .CurrentValue;

            IProperty dependentTenantProperty =
                GetMappedTenantProperty(entry, dependentMapping);
            IProperty principalTenantProperty =
                GetMappedTenantProperty(principal, principalMapping);

            EnsureCanonicalStringTenant(
                dependentTenantId,
                dependentTenantProperty.ClrType,
                "The dependent entity tenant");
            EnsureCanonicalStringTenant(
                principalTenantId,
                principalTenantProperty.ClrType,
                "The related principal tenant");

            if (!ConcurrencyTokenAccessor.ValuesEqual(dependentTenantId, principalTenantId))
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "The relationship target belongs to a different tenant."));
            }

            if (principal.State == EntityState.Unchanged)
            {
                ValidateUnchangedPrincipalDatabaseTenant(principal, principalMapping);
            }
        }
    }

    private void ValidateOwnedTenantRelationships(EntityEntry entry)
    {
        EntityEntry? rootEntry = FindOwnedRootEntry(entry, includeDeleted: true);
        if (rootEntry is null)
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The owner of an owned entity could not be tracked for tenant validation."));
        }

        Type? rootClrType = rootEntry.Metadata.ClrType;
        ITenantEntityMapping? ownerMapping = null;
        object? ownerTenantId = null;
        if (rootClrType is null)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The owned entity '{entry.Metadata.ClrType?.Name ?? "unknown"}' must have "
                    + "a root owner."));
        }

        if (_tenantMappingsByType.TryGetValue(
            rootClrType,
            out ITenantEntityMapping? resolvedOwnerMapping))
        {
            ownerMapping = resolvedOwnerMapping;
            IProperty ownerTenantProperty =
                GetMappedTenantProperty(rootEntry, ownerMapping);
            ownerTenantId = rootEntry
                .Property(ownerMapping.TenantPropertyName)
                .CurrentValue;
            EnsureCanonicalStringTenant(
                ownerTenantId,
                ownerTenantProperty.ClrType,
                "The owned entity owner tenant");

            object? scopedTenantId = GetScopedTenantIdForSave();
            if (scopedTenantId is null
                || !ConcurrencyTokenAccessor.ValuesEqual(
                    ownerTenantId,
                    scopedTenantId))
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "The owned entity owner does not belong to the current tenant scope."));
            }

            if (rootEntry.State == EntityState.Unchanged)
            {
                ValidateUnchangedPrincipalDatabaseTenant(
                    rootEntry,
                    ownerMapping);
            }

            // An owned entry deleted as part of an authorized owner delete is covered by the
            // owner's HardDelete grant. A direct owned delete (owner not deleted) must carry its
            // own grant.
            if (entry.State == EntityState.Deleted
                && rootEntry.State == EntityState.Deleted
                && IsOwnershipCascadeDelete(entry, rootEntry))
            {
                EnsureTenantWriteAuthorized(rootEntry, scopedTenantId);
            }
            else
            {
                EnsureTenantWriteAuthorized(entry, scopedTenantId);
            }
        }
        else if (!_globalEntityTypes.Contains(rootClrType))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The owned entity '{entry.Metadata.ClrType?.Name ?? "unknown"}' must have "
                    + "a tenant-owned or global root owner."));
        }
        else
        {
            EnsureGlobalWriteAuthorized(
                rootEntry.Entity,
                GetRequiredWriteOperation(entry));
        }

        // Global-root owned entries have no tenant scope. They use the same explicit global
        // write authorization as global entities.
        if (entry.State == EntityState.Deleted)
        {
            return;
        }

        foreach (IForeignKey foreignKey in entry.Metadata.GetForeignKeys())
        {
            if (foreignKey.IsOwnership
                || IsInOwnershipChain(
                    entry.Metadata,
                    foreignKey.PrincipalEntityType))
            {
                continue;
            }

            if (entry.State == EntityState.Modified
                && !ForeignKeyChanged(entry, foreignKey))
            {
                continue;
            }

            if (!foreignKey.IsRequired
                && ForeignKeyIsNull(entry, foreignKey))
            {
                continue;
            }

            IEntityType principalType = foreignKey.PrincipalEntityType;
            bool principalTenant = IsTenantOwnedEntityType(principalType);
            bool principalGlobal = IsGlobalEntityType(principalType);

            if (!principalTenant && !principalGlobal)
            {
                throw new SaveChangesSecurityException(
                    new TenantConfigurationFailure(
                        $"The owned entity relationship principal "
                        + $"'{principalType.ClrType?.Name ?? "unknown"}' is neither tenant-owned "
                        + "nor global."));
            }

            if (!principalTenant)
            {
                continue;
            }

            if (ownerMapping is null || ownerTenantId is null)
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "A tenant-owned principal relationship from an owned entity requires "
                        + "a tenant-owned owner."));
            }

            EntityEntry? principal = FindTrackedPrincipal(entry, foreignKey);
            if (principal is null)
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "An owned tenant relationship requires the related principal to be "
                        + "tracked and validated."));
            }

            if (!TryGetEffectiveTenant(
                principal,
                out object? principalTenantId,
                out ITenantEntityMapping? principalMapping,
                out EntityEntry? principalRoot))
            {
                throw new SaveChangesSecurityException(
                    new TenantConfigurationFailure(
                        "The owned relationship principal tenant could not be resolved."));
            }

            EnsureCanonicalStringTenant(
                principalTenantId,
                principalMapping.TenantIdType,
                "The owned relationship principal tenant");

            if (!ConcurrencyTokenAccessor.ValuesEqual(
                ownerTenantId,
                principalTenantId))
            {
                throw new SaveChangesSecurityException(
                    new TenantOwnershipFailure(
                        "The owned entity relationship target belongs to a different tenant."));
            }

            if (principalRoot is not null
                && principalRoot.State == EntityState.Unchanged)
            {
                ValidateUnchangedPrincipalDatabaseTenant(
                    principalRoot,
                    principalMapping);
            }
        }
    }

    private EntityEntry? FindOwnedRootEntry(
        EntityEntry entry,
        bool includeDeleted = false)
    {
        EntityEntry? current = entry;
        while (current is not null && current.Metadata.IsOwned())
        {
            IForeignKey? ownership = current.Metadata.FindOwnership();
            if (ownership is null)
            {
                return null;
            }

            current = FindTrackedPrincipal(
                current,
                ownership,
                includeDeleted);
        }

        return current;
    }

    private bool IsOwnershipCascadeDelete(
        EntityEntry ownedEntry,
        EntityEntry rootEntry)
    {
        EntityEntry? current = ownedEntry;
        while (current is not null
            && !ReferenceEquals(current, rootEntry))
        {
            if (!current.Metadata.IsOwned())
            {
                return false;
            }

            IForeignKey? ownership = current.Metadata.FindOwnership();
            if (ownership is null
                || ownership.DeleteBehavior is not (
                    DeleteBehavior.Cascade
                    or DeleteBehavior.ClientCascade))
            {
                return false;
            }

            current = FindTrackedPrincipal(
                current,
                ownership,
                includeDeleted: true);
        }

        return current is not null;
    }

    private bool TryGetEffectiveTenant(
        EntityEntry entry,
        out object? tenantId,
        out ITenantEntityMapping mapping,
        out EntityEntry? rootEntry)
    {
        tenantId = null;
        mapping = null!;
        rootEntry = FindOwnedRootEntry(entry);
        if (rootEntry?.Metadata.ClrType is not { } clrType
            || !_tenantMappingsByType.TryGetValue(
                clrType,
                out ITenantEntityMapping? resolvedMapping))
        {
            return false;
        }

        mapping = resolvedMapping;
        IProperty tenantProperty = GetMappedTenantProperty(rootEntry, mapping);
        tenantId = rootEntry
            .Property(mapping.TenantPropertyName)
            .CurrentValue;
        EnsureCanonicalStringTenant(
            tenantId,
            tenantProperty.ClrType,
            "The effective owned relationship tenant");
        return true;
    }

    private void ValidateUnchangedPrincipalDatabaseTenant(
        EntityEntry principal,
        ITenantEntityMapping principalMapping)
    {
        PropertyValues? databaseValues;
        try
        {
            databaseValues = principal.GetDatabaseValues();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SaveChangesSecurityException(
                new PersistenceFailure(
                    "The related principal database row could not be verified."));
        }

        if (databaseValues is null)
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The related principal database row could not be verified."));
        }

        if (string.IsNullOrEmpty(principalMapping.TenantPropertyName))
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "Related principal verification requires a direct tenant property mapping."));
        }

        IProperty tenantProperty =
            GetMappedTenantProperty(principal, principalMapping);

        object? databaseTenantId;
        try
        {
            databaseTenantId = databaseValues[principalMapping.TenantPropertyName];
        }
        catch (Exception)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    "The related principal tenant property could not be read for verification."));
        }

        EnsureCanonicalStringTenant(
            databaseTenantId,
            tenantProperty.ClrType,
            "The related principal database tenant");

        object? entityTenantId = principal
            .Property(principalMapping.TenantPropertyName)
            .CurrentValue;
        EnsureCanonicalStringTenant(
            entityTenantId,
            tenantProperty.ClrType,
            "The related principal entity tenant");

        object? scopedTenantId = GetScopedTenantIdForSave();
        if (scopedTenantId is null
            || !ConcurrencyTokenAccessor.ValuesEqual(databaseTenantId, entityTenantId)
            || !ConcurrencyTokenAccessor.ValuesEqual(databaseTenantId, scopedTenantId))
        {
            throw new SaveChangesSecurityException(
                new TenantOwnershipFailure(
                    "The related principal database row belongs to a different tenant."));
        }
    }

    private static bool ForeignKeyChanged(EntityEntry entry, IForeignKey foreignKey)
    {
        foreach (IProperty property in foreignKey.Properties)
        {
            var fkProperty = entry.Property(property.Name);
            if (!ConcurrencyTokenAccessor.ValuesEqual(
                fkProperty.OriginalValue,
                fkProperty.CurrentValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ForeignKeyIsNull(EntityEntry entry, IForeignKey foreignKey)
    {
        foreach (IProperty property in foreignKey.Properties)
        {
            if (entry.Property(property.Name).CurrentValue is not null)
            {
                return false;
            }
        }

        return true;
    }

    private EntityEntry? FindTrackedPrincipal(
        EntityEntry entry,
        IForeignKey foreignKey,
        bool includeDeleted = false)
    {
        EntityEntry? tracked = FindTrackedPrincipalByKey(
            entry,
            foreignKey,
            includeDeleted);
        if (tracked is not null)
        {
            return tracked;
        }

        INavigation? navigation = foreignKey.DependentToPrincipal;
        if (navigation is null)
        {
            return null;
        }

        EntityEntry? target = entry.Reference(navigation.Name).TargetEntry;
        return target is not null
            && target.Metadata.ClrType == foreignKey.PrincipalEntityType.ClrType
            && (includeDeleted || target.State != EntityState.Deleted)
                ? target
                : null;
    }

    private EntityEntry? FindTrackedPrincipalByKey(
        EntityEntry entry,
        IForeignKey foreignKey,
        bool includeDeleted = false)
    {
        IKey principalKey = foreignKey.PrincipalKey;
        var values = new object?[foreignKey.Properties.Count];
        for (int index = 0; index < foreignKey.Properties.Count; index++)
        {
            values[index] = entry.Property(foreignKey.Properties[index].Name).CurrentValue;
        }

        foreach (EntityEntry candidate in ChangeTracker.Entries())
        {
            if (candidate.Metadata.ClrType != foreignKey.PrincipalEntityType.ClrType
                || (!includeDeleted && candidate.State == EntityState.Deleted))
            {
                continue;
            }

            bool matches = true;
            for (int index = 0; index < principalKey.Properties.Count; index++)
            {
                object? principalValue = candidate
                    .Property(principalKey.Properties[index].Name)
                    .CurrentValue;

                if (!ConcurrencyTokenAccessor.ValuesEqual(principalValue, values[index]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void ValidateKeyUnchanged(EntityEntry entry)
    {
        var primaryKey = entry.Metadata.FindPrimaryKey();
        if (primaryKey is null)
        {
            return;
        }

        foreach (IProperty keyProperty in primaryKey.Properties)
        {
            var property = entry.Property(keyProperty.Name);
            if (!ConcurrencyTokenAccessor.ValuesEqual(
                property.OriginalValue,
                property.CurrentValue))
            {
                throw new SaveChangesSecurityException(
                    new MutationInvariantFailure(
                        "primary_key",
                        "The primary key was modified by the entity mutation."));
            }
        }
    }

    private static void ValidateSoftDeleteUnchanged(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDeletable)
        {
            return;
        }

        ValidatePropertyUnchanged(
            entry,
            nameof(ISoftDeletable.IsDeleted),
            "soft_delete_state",
            required: true);
        ValidatePropertyUnchanged(
            entry,
            nameof(ISoftDeletable.DeletedAtUtc),
            "deleted_at",
            required: false);
    }

    private void ValidateConcurrencyTokenUnchanged(EntityEntry entry)
    {
        ConcurrencyTokenAccessor? accessor = GetAccessorOrThrow(entry.Metadata.ClrType);
        if (accessor is not null && accessor.HasChanged(entry))
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "concurrency_token",
                    "The concurrency token was modified by the entity mutation."));
        }
    }

    private static void ValidatePropertyUnchanged(
        EntityEntry entry,
        string propertyName,
        string invariant,
        bool required)
    {
        IProperty? metadata = entry.Metadata.FindProperty(propertyName);
        if (metadata is null)
        {
            if (required)
            {
                throw new SaveChangesSecurityException(
                    new TenantConfigurationFailure(
                        $"The protected property '{propertyName}' is not mapped on '{entry.Metadata.ClrType.Name}'."));
            }

            return;
        }

        var property = entry.Property(propertyName);
        if (!ConcurrencyTokenAccessor.ValuesEqual(
            property.OriginalValue,
            property.CurrentValue))
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    invariant,
                    $"The protected property '{propertyName}' was modified by the entity mutation."));
        }
    }

    private void PrepareConcurrencyTokens()
    {
        EntityEntry[] entries = ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToArray();

        foreach (EntityEntry entry in entries)
        {
            ConcurrencyTokenAccessor? accessor = GetAccessorOrThrow(entry.Metadata.ClrType);
            try
            {
                accessor?.Prepare(entry);
            }
            catch (ConcurrencyConfigurationException exception)
            {
                throw new SaveChangesSecurityException(
                    new ConcurrencyConfigurationFailure(exception.Message));
            }
        }
    }

    private ConcurrencyTokenAccessor? GetAccessorOrThrow(Type entityType)
    {
        try
        {
            return GetConcurrencyTokenAccessor(entityType);
        }
        catch (ConcurrencyConfigurationException exception)
        {
            throw new SaveChangesSecurityException(
                new ConcurrencyConfigurationFailure(exception.Message));
        }
    }

    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (ITenantEntityMapping mapping in _tenantMappings)
        {
            IMutableEntityType? entityType =
                modelBuilder.Model.FindEntityType(mapping.EntityType);
            if (entityType is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' does not match an entity in the model.");
            }

            IProperty tenantProperty =
                ValidateTenantPropertyMapping(entityType, mapping);

            LambdaExpression mappingFilter;
            try
            {
                mappingFilter = mapping.BuildFilter(this);
            }
            catch (TenantConfigurationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new TenantConfigurationException(
                    $"The tenant filter for '{mapping.EntityType.Name}' could not be built.");
            }

            ValidateMappingTenantFilter(mapping, mappingFilter);

            // The framework owns the actual tenant predicate. A custom mapping can describe the
            // tenant property, but its BuildFilter output is never trusted as the isolation boundary.
            LambdaExpression canonicalFilter;
            try
            {
                canonicalFilter =
                    BuildCanonicalTenantFilter(this, mapping.EntityType, tenantProperty);
            }
            catch (TenantConfigurationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new TenantConfigurationException(
                    $"The framework tenant filter for '{mapping.EntityType.Name}' could not be built.");
            }

            modelBuilder.Entity(mapping.EntityType)
                .HasQueryFilter(
                    MecesoftQueryFilterKeys.Tenant,
                    AugmentTenantFilter(mapping, canonicalFilter));
        }
    }

    private static LambdaExpression BuildCanonicalTenantFilter(
        MecesoftDbContext context,
        Type entityType,
        IProperty tenantProperty)
    {
        ParameterExpression parameter = Expression.Parameter(entityType, "entity");
        Expression propertyAccess = Expression.Property(
            parameter,
            tenantProperty.PropertyInfo!);
        Expression contextConstant = Expression.Constant(context);
        Expression tenantValue = Expression.Property(
            contextConstant,
            nameof(MecesoftDbContext.TenantId));

        Type comparableType = tenantProperty.ClrType.IsValueType
            && Nullable.GetUnderlyingType(tenantProperty.ClrType) is null
                ? typeof(Nullable<>).MakeGenericType(tenantProperty.ClrType)
                : tenantProperty.ClrType;

        Expression entityTenant = propertyAccess.Type == comparableType
            ? propertyAccess
            : Expression.Convert(propertyAccess, comparableType);
        Expression currentTenant = Expression.Convert(tenantValue, comparableType);
        Expression hasTenant = Expression.NotEqual(
            tenantValue,
            Expression.Constant(null));

        return Expression.Lambda(
            Expression.AndAlso(hasTenant, Expression.Equal(entityTenant, currentTenant)),
            parameter);
    }

    private static void ValidateMappingTenantFilter(
        ITenantEntityMapping mapping,
        LambdaExpression filter)
    {
        if (filter is null
            || filter.ReturnType != typeof(bool)
            || filter.Parameters.Count != 1
            || filter.Parameters[0].Type != mapping.EntityType)
        {
            throw new TenantConfigurationException(
                $"The tenant filter for '{mapping.EntityType.Name}' has an unsupported shape.");
        }

        bool hasCanonicalEquality = false;
        ValidateMappingFilterConjunct(
            filter.Body,
            mapping,
            filter.Parameters[0],
            ref hasCanonicalEquality);

        if (!hasCanonicalEquality)
        {
            throw new TenantConfigurationException(
                $"The tenant filter for '{mapping.EntityType.Name}' must contain the direct "
                + $"'{mapping.TenantPropertyName}' property equality predicate.");
        }
    }

    private static void ValidateMappingFilterConjunct(
        Expression expression,
        ITenantEntityMapping mapping,
        ParameterExpression parameter,
        ref bool hasCanonicalEquality)
    {
        if (expression is BinaryExpression andAlso
            && andAlso.NodeType == ExpressionType.AndAlso)
        {
            ValidateMappingFilterConjunct(
                andAlso.Left,
                mapping,
                parameter,
                ref hasCanonicalEquality);
            ValidateMappingFilterConjunct(
                andAlso.Right,
                mapping,
                parameter,
                ref hasCanonicalEquality);
            return;
        }

        if (IsCanonicalTenantEquality(expression, mapping, parameter))
        {
            hasCanonicalEquality = true;
            return;
        }

        if (IsContextTenantNullGuard(expression)
            || IsTenantPropertyNullGuard(expression, mapping, parameter))
        {
            return;
        }

        throw new TenantConfigurationException(
            $"The tenant filter for '{mapping.EntityType.Name}' contains a predicate that is not "
            + $"the direct '{mapping.TenantPropertyName}' equality or its null guard.");
    }

    private static bool IsCanonicalTenantEquality(
        Expression expression,
        ITenantEntityMapping mapping,
        ParameterExpression parameter)
    {
        if (expression is not BinaryExpression equality
            || equality.NodeType != ExpressionType.Equal)
        {
            return false;
        }

        return (IsDirectTenantProperty(equality.Left, mapping, parameter)
                && IsContextTenantValue(equality.Right))
            || (IsDirectTenantProperty(equality.Right, mapping, parameter)
                && IsContextTenantValue(equality.Left));
    }

    private static bool IsDirectTenantProperty(
        Expression expression,
        ITenantEntityMapping mapping,
        ParameterExpression parameter)
    {
        expression = UnwrapConversions(expression);
        return expression is MemberExpression member
            && member.Expression is not null
            && UnwrapConversions(member.Expression) == parameter
            && string.Equals(
                member.Member.Name,
                mapping.TenantPropertyName,
                StringComparison.Ordinal);
    }

    private static bool IsContextTenantValue(Expression expression)
    {
        expression = UnwrapConversions(expression);
        return expression is MemberExpression member
            && member.Expression is ConstantExpression constant
            && constant.Value is MecesoftDbContext
            && string.Equals(
                member.Member.Name,
                nameof(MecesoftDbContext.TenantId),
                StringComparison.Ordinal);
    }

    private static bool IsContextTenantNullGuard(Expression expression)
    {
        return expression is BinaryExpression notEqual
            && notEqual.NodeType == ExpressionType.NotEqual
            && ((IsContextTenantValue(notEqual.Left)
                    && IsNullConstant(notEqual.Right))
                || (IsContextTenantValue(notEqual.Right)
                    && IsNullConstant(notEqual.Left)));
    }

    private static bool IsTenantPropertyNullGuard(
        Expression expression,
        ITenantEntityMapping mapping,
        ParameterExpression parameter)
    {
        return expression is BinaryExpression notEqual
            && notEqual.NodeType == ExpressionType.NotEqual
            && ((IsDirectTenantProperty(notEqual.Left, mapping, parameter)
                    && IsNullConstant(notEqual.Right))
                || (IsDirectTenantProperty(notEqual.Right, mapping, parameter)
                    && IsNullConstant(notEqual.Left)));
    }

    private static bool IsNullConstant(Expression expression)
    {
        expression = UnwrapConversions(expression);
        return expression is ConstantExpression constant
            && constant.Value is null;
    }

    private static Expression UnwrapConversions(Expression expression)
    {
        while (expression is UnaryExpression unary
            && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static bool ContainsCanonicalTenantEquality(
        Expression expression,
        ITenantEntityMapping mapping,
        ParameterExpression parameter)
    {
        if (expression is BinaryExpression andAlso
            && andAlso.NodeType == ExpressionType.AndAlso)
        {
            return ContainsCanonicalTenantEquality(
                    andAlso.Left,
                    mapping,
                    parameter)
                || ContainsCanonicalTenantEquality(
                    andAlso.Right,
                    mapping,
                    parameter);
        }

        return IsCanonicalTenantEquality(expression, mapping, parameter);
    }

    private LambdaExpression AugmentTenantFilter(
        ITenantEntityMapping mapping,
        LambdaExpression tenantFilter)
    {
        if (_tenantFilterAugmenter is null)
        {
            return tenantFilter;
        }

        LambdaExpression augmentedFilter;
        try
        {
            augmentedFilter = _tenantFilterAugmenter.Augment(
                this,
                mapping,
                tenantFilter);
        }
        catch (TenantConfigurationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TenantConfigurationException(
                $"The tenant filter augmenter failed for '{mapping.EntityType.Name}'.");
        }

        if (augmentedFilter is null
            || augmentedFilter.ReturnType != typeof(bool)
            || augmentedFilter.Parameters.Count != 1
            || augmentedFilter.Parameters[0].Type != mapping.EntityType)
        {
            throw new TenantConfigurationException(
                $"The tenant filter augmenter returned an invalid filter for "
                + $"'{mapping.EntityType.Name}'.");
        }

        if (!ContainsCanonicalTenantEquality(
            augmentedFilter.Body,
            mapping,
            augmentedFilter.Parameters[0]))
        {
            throw new TenantConfigurationException(
                $"The tenant filter augmenter removed the direct tenant equality for "
                + $"'{mapping.EntityType.Name}'.");
        }

        return augmentedFilter;
    }

    private void ApplySoftDeleteFilters(ModelBuilder modelBuilder)
    {
        var explicitMappings = _softDeleteMappings
            .GroupBy(mapping => mapping.EntityType)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            Type? clrType = entityType.ClrType;
            if (clrType is null
                || entityType.IsOwned()
                || !typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                continue;
            }

            LambdaExpression filter = explicitMappings.TryGetValue(clrType, out ISoftDeleteEntityMapping? mapping)
                ? mapping.BuildFilter()
                : BuildDefaultSoftDeleteFilter(clrType);

            modelBuilder.Entity(clrType)
                .HasQueryFilter(MecesoftQueryFilterKeys.SoftDelete, filter);
        }
    }

    private static HashSet<Type> BuildGlobalEntityTypes(ModelBuilder modelBuilder)
        => BuildGlobalEntityTypes(modelBuilder.Model);

    private static HashSet<Type> BuildGlobalEntityTypes(IReadOnlyModel model)
    {
        var globalTypes = new HashSet<Type>();
        foreach (IReadOnlyEntityType entityType in model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(GlobalEntityModelBuilderExtensions.AnnotationName)?.Value is not true)
            {
                continue;
            }

            if (entityType.IsOwned())
            {
                throw new TenantConfigurationException(
                    $"The owned entity type '{entityType.ClrType?.Name}' cannot be declared global.");
            }

            if (entityType.ClrType is null)
            {
                throw new TenantConfigurationException(
                    "A global entity must have a CLR type.");
            }

            globalTypes.Add(entityType.ClrType);
        }

        return globalTypes;
    }

    private bool IsTenantRelated(Type clrType)
    {
        _tenantRelatedTypes ??= BuildTenantRelatedTypes(Model);
        return _tenantRelatedTypes.Contains(clrType);
    }

    private HashSet<Type> BuildTenantRelatedTypes(IReadOnlyModel model)
    {
        var entityTypes = model.GetEntityTypes()
            .Where(entityType => !entityType.IsOwned() && entityType.ClrType is not null)
            .ToDictionary(entityType => entityType.ClrType!, entityType => entityType);

        var related = new HashSet<Type>();
        var pending = new Queue<Type>();

        foreach (Type tenantType in _tenantMappingsByType.Keys)
        {
            if (entityTypes.ContainsKey(tenantType) && related.Add(tenantType))
            {
                pending.Enqueue(tenantType);
            }
        }

        void AddRelated(Type? type)
        {
            if (type is not null
                && !_globalEntityTypes.Contains(type)
                && entityTypes.ContainsKey(type)
                && related.Add(type))
            {
                pending.Enqueue(type);
            }
        }

        while (pending.Count > 0)
        {
            Type current = pending.Dequeue();
            if (!entityTypes.TryGetValue(current, out var metadata))
            {
                continue;
            }

            foreach (var foreignKey in metadata.GetForeignKeys())
            {
                AddRelated(foreignKey.PrincipalEntityType.ClrType);
            }

            foreach (var foreignKey in metadata.GetReferencingForeignKeys())
            {
                AddRelated(foreignKey.DeclaringEntityType.ClrType);
            }

            foreach (var navigation in metadata.GetNavigations())
            {
                AddRelated(navigation.TargetEntityType.ClrType);
            }

            foreach (var navigation in metadata.GetSkipNavigations())
            {
                AddRelated(navigation.TargetEntityType.ClrType);
            }
        }

        return related;
    }

    private void ValidateMappings(ModelBuilder modelBuilder)
    {
        var modelEntityTypes = modelBuilder.Model.GetEntityTypes()
            .Where(entityType => !entityType.IsOwned() && entityType.ClrType is not null)
            .ToArray();
        HashSet<Type> modelTypes = modelEntityTypes
            .Select(entityType => entityType.ClrType!)
            .ToHashSet();

        ValidateTenantMappings(modelBuilder, modelEntityTypes, modelTypes);
        ValidateSoftDeleteMappings(modelBuilder, modelEntityTypes, modelTypes);
        ValidateOwnedSoftDeletePolicy(modelBuilder.Model);
        ValidateGlobalMappings(modelEntityTypes, modelTypes);
        ValidateRelatedEntityMappings(modelBuilder.Model);
        ValidateOwnedEntityRelationships(modelBuilder.Model);
        ValidateCascadeDeletePolicy(modelEntityTypes);
    }

    private static void ValidateOwnedSoftDeletePolicy(IReadOnlyModel model)
    {
        foreach (IReadOnlyEntityType entityType in model.GetEntityTypes())
        {
            if (!entityType.IsOwned() || entityType.ClrType is not { } clrType)
            {
                continue;
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                throw new TenantConfigurationException(
                    $"The owned entity '{clrType.Name}' cannot implement ISoftDeletable. "
                    + "Owned soft-delete is not supported; apply soft-delete to the ownership "
                    + "root or use a separate tenant-owned entity.");
            }
        }
    }

    private void ValidateRelatedEntityMappings(IReadOnlyModel model)
    {
        foreach (Type relatedType in BuildTenantRelatedTypes(model))
        {
            if (_tenantMappingsByType.ContainsKey(relatedType))
            {
                continue;
            }

            throw new TenantConfigurationException(
                $"The related entity '{relatedType.Name}' has no tenant mapping or global declaration.");
        }
    }

    private void ValidateOwnedEntityRelationships(IReadOnlyModel model)
    {
        foreach (IEntityType ownedType in model.GetEntityTypes()
            .Where(entityType => entityType.IsOwned()
                && entityType.ClrType is not null))
        {
            IReadOnlyEntityType? root = GetOwnershipRoot(ownedType);
            if (root?.ClrType is null)
            {
                throw new TenantConfigurationException(
                    $"The owned entity '{ownedType.ClrType.Name}' has no valid ownership root.");
            }

            bool rootTenant = IsTenantOwnedEntityType(root);
            bool rootGlobal = IsGlobalEntityType(root);

            foreach (IForeignKey foreignKey in ownedType.GetForeignKeys())
            {
                if (foreignKey.IsOwnership
                    || IsInOwnershipChain(ownedType, foreignKey.PrincipalEntityType))
                {
                    continue;
                }

                IEntityType principal = foreignKey.PrincipalEntityType;
                bool cascade = IsCascade(foreignKey);
                IReadOnlyEntityType? principalRoot = GetOwnershipRoot(principal);
                bool principalRootTenant = principalRoot is not null
                    && IsTenantOwnedEntityType(principalRoot);
                bool principalRootGlobal = principalRoot is not null
                    && IsGlobalEntityType(principalRoot);
                bool sameRoot = ReferenceEquals(root, principalRoot);

                if (principal.IsOwned())
                {
                    if (sameRoot)
                    {
                        continue;
                    }

                    if (rootTenant || principalRootTenant)
                    {
                        if (cascade)
                        {
                            throw new TenantConfigurationException(
                                $"The owned entity '{ownedType.ClrType.Name}' cannot use cascade "
                                + $"delete to the owned entity '{principal.ClrType?.Name}' from a "
                                + "different ownership root. Use Restrict or NoAction.");
                        }

                        // Runtime validation compares the two owner tenants.
                        continue;
                    }

                    if (rootGlobal && principalRootGlobal)
                    {
                        if (cascade)
                        {
                            throw new TenantConfigurationException(
                                $"The owned entity '{ownedType.ClrType.Name}' cannot use cascade "
                                + $"delete to the global-owned entity '{principal.ClrType?.Name}'. "
                                + "Use Restrict or NoAction.");
                        }

                        continue;
                    }

                    throw new TenantConfigurationException(
                        $"The owned entity '{ownedType.ClrType.Name}' cannot reference the owned "
                        + $"entity '{principal.ClrType?.Name}' from a different ownership root.");
                }

                if (IsTenantOwnedEntityType(principal))
                {
                    if (!rootTenant)
                    {
                        throw new TenantConfigurationException(
                            $"The owned entity '{ownedType.ClrType.Name}' of the non-tenant owner "
                            + $"'{root.ClrType.Name}' cannot reference the tenant-owned entity "
                            + $"'{principal.ClrType?.Name}'.");
                    }

                    if (cascade)
                    {
                        throw new TenantConfigurationException(
                            $"The owned entity '{ownedType.ClrType.Name}' cannot use cascade "
                            + $"delete from the tenant-owned entity '{principal.ClrType?.Name}' "
                            + "outside its ownership chain. Use Restrict or NoAction.");
                    }

                    // Runtime validation compares owner and principal tenants.
                    continue;
                }

                if (IsGlobalEntityType(principal))
                {
                    if (rootTenant && cascade)
                    {
                        throw new TenantConfigurationException(
                            $"The owned entity '{ownedType.ClrType.Name}' of tenant owner "
                            + $"'{root.ClrType.Name}' cannot use cascade delete from the global "
                            + $"entity '{principal.ClrType?.Name}'. Use Restrict or NoAction.");
                    }

                    continue;
                }

                throw new TenantConfigurationException(
                    $"The owned entity '{ownedType.ClrType.Name}' references "
                    + $"'{principal.ClrType?.Name}' which is neither tenant-owned nor global.");
            }
        }
    }

    private static bool IsCascade(IForeignKey foreignKey)
    {
        return foreignKey.DeleteBehavior is DeleteBehavior.Cascade
            or DeleteBehavior.ClientCascade;
    }

    private static IReadOnlyEntityType? GetOwnershipRoot(IReadOnlyEntityType entityType)
    {
        IReadOnlyEntityType? current = entityType;
        while (current is not null && current.IsOwned())
        {
            IReadOnlyForeignKey? ownership = current.FindOwnership();
            if (ownership is null)
            {
                return null;
            }

            current = ownership.PrincipalEntityType;
        }

        return current;
    }

    private static bool IsInOwnershipChain(
        IReadOnlyEntityType ownedType,
        IReadOnlyEntityType principalType)
    {
        IReadOnlyEntityType? current = ownedType;
        while (current is not null)
        {
            if (ReferenceEquals(current, principalType))
            {
                return true;
            }

            IReadOnlyForeignKey? ownership = current.FindOwnership();
            if (ownership is null)
            {
                return false;
            }

            current = ownership.PrincipalEntityType;
        }

        return false;
    }

    private bool IsTenantOwnedEntityType(IReadOnlyEntityType entityType)
    {
        IReadOnlyEntityType? root = GetOwnershipRoot(entityType);
        Type? clrType = root?.ClrType;
        return clrType is not null
            && (_tenantMappingsByType.ContainsKey(clrType)
                || ImplementsTenantEntity(clrType));
    }

    private bool IsGlobalEntityType(IReadOnlyEntityType entityType)
    {
        IReadOnlyEntityType? root = GetOwnershipRoot(entityType);
        Type? clrType = root?.ClrType;
        return clrType is not null && _globalEntityTypes.Contains(clrType);
    }

    private void ValidateCascadeDeletePolicy(
        IReadOnlyCollection<IMutableEntityType> modelEntityTypes)
    {
        foreach (IMutableEntityType entityType in modelEntityTypes)
        {
            Type? dependentType = entityType.ClrType;
            if (dependentType is null)
            {
                continue;
            }

            foreach (IForeignKey foreignKey in entityType.GetForeignKeys())
            {
                if (foreignKey.IsOwnership
                    || foreignKey.DeleteBehavior is not (
                        DeleteBehavior.Cascade
                        or DeleteBehavior.ClientCascade))
                {
                    continue;
                }

                IEntityType principalEntityType = foreignKey.PrincipalEntityType;
                Type? principalType = principalEntityType.ClrType;
                if (principalType is null)
                {
                    continue;
                }

                if (IsGlobalEntityType(principalEntityType)
                    && IsTenantOwnedEntityType(entityType))
                {
                    throw new TenantConfigurationException(
                        $"The tenant-owned entity '{dependentType.Name}' cannot use cascade delete "
                        + $"from the global entity '{principalType.Name}'. Use Restrict or NoAction.");
                }

                if (typeof(ISoftDeletable).IsAssignableFrom(dependentType)
                    && !typeof(ISoftDeletable).IsAssignableFrom(principalType))
                {
                    throw new TenantConfigurationException(
                        $"The soft-deletable entity '{dependentType.Name}' cannot use cascade delete "
                        + $"from the non-soft-deletable entity '{principalType.Name}'. Use Restrict instead.");
                }
            }
        }
    }

    private void ValidateGlobalMappings(
        IReadOnlyCollection<IMutableEntityType> modelEntityTypes,
        HashSet<Type> modelTypes)
    {
        foreach (Type globalType in _globalEntityTypes)
        {
            if (!modelTypes.Contains(globalType))
            {
                throw new TenantConfigurationException(
                    $"The global entity '{globalType.Name}' is not part of the DbContext model.");
            }

            if (_tenantMappingsByType.ContainsKey(globalType))
            {
                throw new TenantConfigurationException(
                    $"The global entity '{globalType.Name}' cannot also have a tenant mapping.");
            }

            if (ImplementsTenantEntity(globalType))
            {
                throw new TenantConfigurationException(
                    $"The global entity '{globalType.Name}' cannot implement a tenant entity contract.");
            }

            IMutableEntityType entityType = modelEntityTypes
                .Single(candidate => candidate.ClrType == globalType);

            foreach (IForeignKey foreignKey in entityType.GetForeignKeys())
            {
                Type principalType = foreignKey.PrincipalEntityType.ClrType;
                if (_tenantMappingsByType.ContainsKey(principalType)
                    || ImplementsTenantEntity(principalType))
                {
                    throw new TenantConfigurationException(
                        $"The global entity '{globalType.Name}' cannot reference the tenant-owned entity '{principalType.Name}'.");
                }
            }

            foreach (INavigation navigation in entityType.GetSkipNavigations())
            {
                Type targetType = navigation.TargetEntityType.ClrType;
                if (_tenantMappingsByType.ContainsKey(targetType)
                    || ImplementsTenantEntity(targetType))
                {
                    throw new TenantConfigurationException(
                        $"The global entity '{globalType.Name}' cannot have a many-to-many relationship with the tenant-owned entity '{targetType.Name}'.");
                }
            }

            foreach (IForeignKey foreignKey in entityType.GetReferencingForeignKeys())
            {
                Type dependentType = foreignKey.DeclaringEntityType.ClrType;
                if (foreignKey.DeclaringEntityType.IsOwned()
                    || _globalEntityTypes.Contains(dependentType)
                    || _tenantMappingsByType.ContainsKey(dependentType)
                    || ImplementsTenantEntity(dependentType))
                {
                    continue;
                }

                throw new TenantConfigurationException(
                    $"The entity '{dependentType.Name}' cannot depend on the global entity '{globalType.Name}' "
                    + "unless it is explicitly global or tenant-owned.");
            }
        }
    }

    private static IProperty ValidateTenantPropertyMapping(
        IReadOnlyEntityType entityType,
        ITenantEntityMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.TenantPropertyName))
        {
            throw new TenantConfigurationException(
                $"The tenant mapping for '{entityType.ClrType.Name}' must select a direct tenant property.");
        }

        IProperty? property = entityType.FindProperty(mapping.TenantPropertyName)
            as IProperty;
        if (property is null)
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' is not mapped.");
        }

        if (property.PropertyInfo is null || property.IsIndexerProperty())
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' must map to a CLR property.");
        }

        if (!IsTenantContractProperty(entityType.ClrType, property.PropertyInfo))
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' must be the ITenantEntity.TenantId contract property.");
        }

        if (mapping.TenantIdType != typeof(object)
            && mapping.TenantIdType != property.ClrType)
        {
            throw new TenantConfigurationException(
                $"The declared tenant identifier type '{mapping.TenantIdType.Name}' does not match "
                + $"the mapped property type '{property.ClrType.Name}' on "
                + $"'{entityType.ClrType.Name}'.");
        }

        if (property.GetValueConverter() is not null)
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' must not use a value converter.");
        }

        if (property.GetComputedColumnSql() is not null)
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' must not be a computed column.");
        }

        if (string.IsNullOrWhiteSpace(entityType.GetTableName())
            || !string.IsNullOrWhiteSpace(entityType.GetViewName()))
        {
            throw new TenantConfigurationException(
                $"The tenant entity '{entityType.ClrType.Name}' must map to a table rather than a view.");
        }

        if (string.IsNullOrWhiteSpace(property.GetColumnName()))
        {
            throw new TenantConfigurationException(
                $"The tenant property '{mapping.TenantPropertyName}' on "
                + $"'{entityType.ClrType.Name}' is not mapped to a table column.");
        }

        return property;
    }

    private static bool IsTenantContractProperty(
        Type entityType,
        System.Reflection.PropertyInfo property)
    {
        foreach (Type contract in entityType.GetInterfaces())
        {
            if (!contract.IsGenericType
                || contract.GetGenericTypeDefinition() != typeof(ITenantEntity<>))
            {
                continue;
            }

            System.Reflection.PropertyInfo? contractProperty = contract.GetProperty(
                nameof(ITenantEntity<object>.TenantId));
            System.Reflection.MethodInfo? contractGetter = contractProperty?.GetGetMethod();
            if (contractGetter is null)
            {
                continue;
            }

            InterfaceMapping map;
            try
            {
                map = entityType.GetInterfaceMap(contract);
            }
            catch (ArgumentException)
            {
                continue;
            }

            int index = Array.IndexOf(map.InterfaceMethods, contractGetter);
            if (index < 0)
            {
                continue;
            }

            System.Reflection.MethodInfo target = map.TargetMethods[index];
            System.Reflection.PropertyInfo? implemented = target.DeclaringType?
                .GetProperties(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    candidate.GetGetMethod(nonPublic: true)?.MetadataToken
                        == target.MetadataToken
                    && candidate.GetGetMethod(nonPublic: true)?.Module
                        == target.Module);

            if (implemented is not null
                && implemented.MetadataToken == property.MetadataToken
                && implemented.Module == property.Module
                && implemented.DeclaringType == property.DeclaringType)
            {
                return true;
            }
        }

        return false;
    }

    private void ValidateTenantMappings(
        ModelBuilder modelBuilder,
        IReadOnlyCollection<Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType> modelEntityTypes,
        HashSet<Type> modelTypes)
    {
        Type[] duplicates = _tenantMappings
            .GroupBy(mapping => mapping.EntityType)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new TenantConfigurationException(
                $"Multiple tenant mappings were registered for: {string.Join(", ", duplicates.Select(type => type.Name))}.");
        }

        foreach (ITenantEntityMapping mapping in _tenantMappings)
        {
            if (!modelTypes.Contains(mapping.EntityType))
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' does not match an entity in the model.");
            }

            IProperty tenantProperty = ValidateTenantPropertyMapping(
                modelBuilder.Entity(mapping.EntityType).Metadata,
                mapping);

            bool requiresVerifier = mapping.TenantIdType == typeof(string)
                || mapping.TenantIdType == typeof(object)
                || tenantProperty.ClrType == typeof(string);

            ITenantCollationVerifier? verifier = ResolveTenantCollationVerifier();

            if (requiresVerifier && verifier is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' uses string tenant identifiers "
                    + "and requires a runtime tenant collation verifier.");
            }

            if (requiresVerifier
                && _requireTrustedTenantVerifier
                && verifier is not ITrustedTenantCollationVerifier)
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' requires the trusted "
                    + "provider production collation verifier.");
            }

            if (requiresVerifier
                && _requireExactTenantFilters
                && _tenantFilterAugmenter is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant mapping for '{mapping.EntityType.Name}' requires the provider "
                    + "exact tenant filter augmenter.");
            }

            if (modelBuilder.Entity(mapping.EntityType).Metadata
                .FindDeclaredQueryFilter(MecesoftQueryFilterKeys.Tenant) is null)
            {
                throw new TenantConfigurationException(
                    $"The tenant filter is missing for '{mapping.EntityType.Name}'.");
            }
        }

        HashSet<Type> mappedTypes = _tenantMappings
            .Select(mapping => mapping.EntityType)
            .ToHashSet();

        foreach (var entityType in modelEntityTypes)
        {
            Type clrType = entityType.ClrType!;
            if (ImplementsTenantEntity(clrType) && !mappedTypes.Contains(clrType))
            {
                throw new TenantConfigurationException(
                    $"The tenant entity '{clrType.Name}' has no tenant mapping.");
            }
        }
    }

    private void ValidateSoftDeleteMappings(
        ModelBuilder modelBuilder,
        IReadOnlyCollection<Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType> modelEntityTypes,
        HashSet<Type> modelTypes)
    {
        Type[] duplicates = _softDeleteMappings
            .GroupBy(mapping => mapping.EntityType)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new TenantConfigurationException(
                $"Multiple soft-delete mappings were registered for: {string.Join(", ", duplicates.Select(type => type.Name))}.");
        }

        foreach (ISoftDeleteEntityMapping mapping in _softDeleteMappings)
        {
            if (!modelTypes.Contains(mapping.EntityType))
            {
                throw new TenantConfigurationException(
                    $"The soft-delete mapping for '{mapping.EntityType.Name}' does not match an entity in the model.");
            }
        }

        foreach (var entityType in modelEntityTypes)
        {
            Type clrType = entityType.ClrType!;
            if (!typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                continue;
            }

            if (modelBuilder.Entity(clrType).Metadata
                .FindDeclaredQueryFilter(MecesoftQueryFilterKeys.SoftDelete) is null)
            {
                throw new TenantConfigurationException(
                    $"The soft-delete filter is missing for '{clrType.Name}'.");
            }
        }
    }

    private static LambdaExpression BuildDefaultSoftDeleteFilter(Type clrType)
    {
        ParameterExpression parameter = Expression.Parameter(clrType, "entity");
        MemberExpression isDeleted = Expression.Property(
            parameter,
            nameof(ISoftDeletable.IsDeleted));
        UnaryExpression body = Expression.Not(isDeleted);
        return Expression.Lambda(body, parameter);
    }

    private static bool ImplementsTenantEntity(Type clrType)
    {
        return clrType.GetInterfaces().Any(
            type => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(ITenantEntity<>));
    }
}
