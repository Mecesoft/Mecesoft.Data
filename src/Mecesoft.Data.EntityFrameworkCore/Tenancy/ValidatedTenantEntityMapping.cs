using System.Linq.Expressions;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Immutable snapshot of a tenant mapping taken before model creation.
/// </summary>
/// <remarks>
/// Custom <see cref="ITenantEntityMapping"/> implementations are not required to be immutable.
/// Runtime validation must not read mutable mapping metadata after the model has been created,
/// because a mapping could otherwise change the property or identifier type used for write and
/// verifier checks while the model filter still targets the original property.
/// </remarks>
internal abstract class ValidatedTenantEntityMappingBase : ITenantEntityMapping
{
    private readonly ITenantEntityMapping _source;

    protected ValidatedTenantEntityMappingBase(ITenantEntityMapping source)
    {
        ArgumentNullException.ThrowIfNull(source);
        TenantMappingContract.Validate(source);
        _source = source;
        EntityType = source.EntityType;
        TenantIdType = source.TenantIdType;
        TenantPropertyName = source.TenantPropertyName;
        SourceType = source.GetType();
    }

    /// <summary>
    /// Gets the concrete source mapping type used for model-cache identity.
    /// </summary>
    internal Type SourceType { get; }

    /// <inheritdoc />
    public Type EntityType { get; }

    /// <inheritdoc />
    public Type TenantIdType { get; }

    /// <inheritdoc />
    public string TenantPropertyName { get; }

    /// <inheritdoc />
    public object? GetTenantId(object entity)
        => _source.GetTenantId(entity);

    /// <inheritdoc />
    public LambdaExpression BuildFilter(MecesoftDbContext context)
        => _source.BuildFilter(context);

    public static ITenantEntityMapping Create(ITenantEntityMapping source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is ValidatedTenantEntityMappingBase)
        {
            return source;
        }

        return source is ITenantExactMapping
            ? new ValidatedExactTenantEntityMapping(source)
            : new ValidatedTenantEntityMapping(source);
    }
}

internal sealed class ValidatedTenantEntityMapping
    : ValidatedTenantEntityMappingBase
{
    public ValidatedTenantEntityMapping(ITenantEntityMapping source)
        : base(source)
    {
    }
}

internal sealed class ValidatedExactTenantEntityMapping
    : ValidatedTenantEntityMappingBase, ITenantExactMapping
{
    public ValidatedExactTenantEntityMapping(ITenantEntityMapping source)
        : base(source)
    {
    }
}
