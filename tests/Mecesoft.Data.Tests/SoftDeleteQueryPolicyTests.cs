using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.SoftDelete;

namespace Mecesoft.Data.Tests;

public sealed class SoftDeleteQueryPolicyTests
{
    [Fact]
    public void ActiveQueryRequiresTenantAndNotDeleted()
    {
        Guid tenantA = Guid.NewGuid();
        Func<SoftDeleteEntity, bool> predicate = Compose(tenantA, requireDeleted: false);

        Assert.True(predicate(new SoftDeleteEntity { TenantId = tenantA, IsDeleted = false }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = tenantA, IsDeleted = true }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = Guid.NewGuid(), IsDeleted = false }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = Guid.NewGuid(), IsDeleted = true }));
    }

    [Fact]
    public void RestoreQueryRequiresTenantAndDeleted()
    {
        Guid tenantA = Guid.NewGuid();
        Func<SoftDeleteEntity, bool> predicate = Compose(tenantA, requireDeleted: true);

        Assert.False(predicate(new SoftDeleteEntity { TenantId = tenantA, IsDeleted = false }));
        Assert.True(predicate(new SoftDeleteEntity { TenantId = tenantA, IsDeleted = true }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = Guid.NewGuid(), IsDeleted = false }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = Guid.NewGuid(), IsDeleted = true }));
    }

    [Fact]
    public void ComposedPredicateStillRequiresTenantWhenSoftDeletePredicateAlwaysMatches()
    {
        Guid tenantA = Guid.NewGuid();
        Expression<Func<SoftDeleteEntity, bool>> alwaysDeleted = _ => true;

        Func<SoftDeleteEntity, bool> predicate = SoftDeleteQueryPolicy
            .Compose<SoftDeleteEntity, Guid>(
                entity => entity.TenantId,
                tenantA,
                alwaysDeleted,
                requireDeleted: true)
            .Compile();

        Assert.True(predicate(new SoftDeleteEntity { TenantId = tenantA }));
        Assert.False(predicate(new SoftDeleteEntity { TenantId = Guid.NewGuid() }));
    }

    [Fact]
    public void NullSelectorsAreRejected()
    {
        Guid tenantA = Guid.NewGuid();

        Assert.Throws<ArgumentNullException>(
            () => SoftDeleteQueryPolicy.Compose<SoftDeleteEntity, Guid>(
                null!,
                tenantA,
                entity => entity.IsDeleted,
                requireDeleted: false));

        Assert.Throws<ArgumentNullException>(
            () => SoftDeleteQueryPolicy.Compose<SoftDeleteEntity, Guid>(
                entity => entity.TenantId,
                tenantA,
                null!,
                requireDeleted: false));
    }

    private static Func<SoftDeleteEntity, bool> Compose(Guid tenantId, bool requireDeleted)
    {
        return SoftDeleteQueryPolicy
            .Compose<SoftDeleteEntity, Guid>(
                entity => entity.TenantId,
                tenantId,
                entity => entity.IsDeleted,
                requireDeleted)
            .Compile();
    }

    private sealed class SoftDeleteEntity : ISoftDeletable
    {
        public Guid TenantId { get; init; }

        public bool IsDeleted { get; init; }

        public DateTimeOffset? DeletedAtUtc { get; init; }
    }
}
