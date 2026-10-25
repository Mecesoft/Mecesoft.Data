using System.Linq.Expressions;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;

namespace Mecesoft.Data.EntityFrameworkCore.Tenancy;

/// <summary>
/// Immutable snapshot of a soft-delete mapping taken before model creation.
/// </summary>
/// <remarks>
/// The filter and its structural fingerprint are captured once. A mutable custom mapping cannot
/// change the visibility predicate after model creation, and the fingerprint participates in the EF
/// model cache identity.
/// </remarks>
internal sealed class ValidatedSoftDeleteEntityMapping : ISoftDeleteEntityMapping
{
    private readonly LambdaExpression _filter;

    private ValidatedSoftDeleteEntityMapping(
        Type entityType,
        Type sourceType,
        LambdaExpression filter,
        string fingerprint)
    {
        EntityType = entityType;
        SourceType = sourceType;
        _filter = filter;
        Fingerprint = fingerprint;
    }

    /// <inheritdoc />
    public Type EntityType { get; }

    internal Type SourceType { get; }

    internal string Fingerprint { get; }

    /// <inheritdoc />
    public LambdaExpression BuildFilter() => _filter;

    public static ValidatedSoftDeleteEntityMapping Create(
        ISoftDeleteEntityMapping source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Type entityType = source.EntityType
            ?? throw new TenantConfigurationException(
                "A soft-delete mapping must declare an entity type.");

        LambdaExpression filter;
        try
        {
            filter = source.BuildFilter();
        }
        catch (TenantConfigurationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TenantConfigurationException(
                $"The soft-delete filter for '{entityType.Name}' could not be built.");
        }

        if (filter is null
            || filter.ReturnType != typeof(bool)
            || filter.Parameters.Count != 1
            || filter.Parameters[0].Type != entityType)
        {
            throw new TenantConfigurationException(
                $"The soft-delete filter for '{entityType.Name}' has an unsupported shape.");
        }

        string fingerprint;
        try
        {
            fingerprint = ExpressionFingerprint.Create(filter);
        }
        catch (TenantConfigurationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new TenantConfigurationException(
                $"The soft-delete filter for '{entityType.Name}' could not be fingerprinted.");
        }

        return new ValidatedSoftDeleteEntityMapping(
            entityType,
            source.GetType(),
            filter,
            fingerprint);
    }
}
