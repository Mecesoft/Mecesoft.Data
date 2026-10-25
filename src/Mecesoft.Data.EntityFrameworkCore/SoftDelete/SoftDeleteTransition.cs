using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Concurrency;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Represents the single soft-delete state transition authorized for the next SaveChanges call.
/// </summary>
/// <remarks>
/// This type is internal on purpose. It is created only by the secure soft-delete repository and is
/// consumed by the central SaveChanges validation. No public API can create a transition grant.
/// </remarks>
internal sealed class SoftDeleteTransition
{
    private readonly object _entity;

    public SoftDeleteTransition(
        object entity,
        SoftDeleteOperation operation,
        DateTimeOffset? deletedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (operation is not (SoftDeleteOperation.SoftDelete or SoftDeleteOperation.Restore))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Only soft-delete and restore transitions can be authorized.");
        }

        _entity = entity;
        Operation = operation;
        DeletedAtUtc = deletedAtUtc;
    }

    public SoftDeleteOperation Operation { get; }

    public DateTimeOffset? DeletedAtUtc { get; }

    public bool Matches(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ReferenceEquals(entry.Entity, _entity);
    }

    public void Validate(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        PropertyEntry isDeleted = GetRequiredProperty(
            entry,
            nameof(ISoftDeletable.IsDeleted));
        PropertyEntry deletedAtUtc = GetRequiredProperty(
            entry,
            nameof(ISoftDeletable.DeletedAtUtc));

        bool valid = Operation switch
        {
            SoftDeleteOperation.SoftDelete =>
                ConcurrencyTokenAccessor.ValuesEqual(isDeleted.OriginalValue, false)
                && ConcurrencyTokenAccessor.ValuesEqual(isDeleted.CurrentValue, true)
                && deletedAtUtc.OriginalValue is null
                && ConcurrencyTokenAccessor.ValuesEqual(deletedAtUtc.CurrentValue, DeletedAtUtc),
            SoftDeleteOperation.Restore =>
                ConcurrencyTokenAccessor.ValuesEqual(isDeleted.OriginalValue, true)
                && ConcurrencyTokenAccessor.ValuesEqual(isDeleted.CurrentValue, false)
                && deletedAtUtc.CurrentValue is null,
            _ => false,
        };

        if (!valid)
        {
            throw new SaveChangesSecurityException(
                new MutationInvariantFailure(
                    "soft_delete_transition",
                    "The authorized soft-delete transition is not a valid state change."));
        }
    }

    private static PropertyEntry GetRequiredProperty(EntityEntry entry, string propertyName)
    {
        IProperty? property = entry.Metadata.FindProperty(propertyName);
        if (property is null)
        {
            throw new SaveChangesSecurityException(
                new TenantConfigurationFailure(
                    $"The soft-delete property '{propertyName}' is not mapped."));
        }

        return entry.Property(propertyName);
    }
}
