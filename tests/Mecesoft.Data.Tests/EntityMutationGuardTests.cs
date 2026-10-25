using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Tests;

public sealed class EntityMutationGuardTests
{
    [Fact]
    public void GuardRejectsEachProtectedInvariantChange()
    {
        AssertRejected(entity => entity.Id = Guid.NewGuid(), "primary_key");
        AssertRejected(entity => entity.TenantId = Guid.NewGuid(), "tenant_id");
        AssertRejected(entity => entity.IsDeleted = true, "soft_delete_state");
        AssertRejected(entity => entity.DeletedAtUtc = DateTimeOffset.UtcNow, "deleted_at");
        AssertRejected(entity => entity.Token = new ConcurrencyToken("v2"), "concurrency_token");
    }

    [Fact]
    public void GuardAllowsUnprotectedMutation()
    {
        MutableEntity entity = NewEntity();
        EntityMutationGuard<MutableEntity> guard = CreateGuard(entity);

        entity.Name = "changed";

        DataResult<bool> result = guard.Validate(entity);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public void GuardRejectsNullEntity()
    {
        Assert.Throws<ArgumentNullException>(
            () => new EntityMutationGuard<MutableEntity>(null!, item => item.Id));
        Assert.Throws<ArgumentNullException>(
            () => new EntityMutationGuard<MutableEntity>(NewEntity(), null!));
        Assert.Throws<ArgumentNullException>(() => CreateGuard(NewEntity()).Validate(null!));
    }

    private static void AssertRejected(Action<MutableEntity> mutate, string expectedInvariant)
    {
        MutableEntity entity = NewEntity();
        EntityMutationGuard<MutableEntity> guard = CreateGuard(entity);

        mutate(entity);
        DataResult<bool> result = guard.Validate(entity);

        Assert.True(result.IsFailure);
        MutationInvariantFailure failure = Assert.IsType<MutationInvariantFailure>(result.Failure);
        Assert.Equal(expectedInvariant, failure.Invariant);
    }

    private static EntityMutationGuard<MutableEntity> CreateGuard(MutableEntity entity)
    {
        return new EntityMutationGuard<MutableEntity>(
            entity,
            primaryKeySelector: item => item.Id,
            tenantIdSelector: item => item.TenantId,
            isDeletedSelector: item => item.IsDeleted,
            deletedAtUtcSelector: item => item.DeletedAtUtc,
            concurrencyTokenSelector: item => item.Token);
    }

    private static MutableEntity NewEntity()
    {
        return new MutableEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = "original",
            Token = new ConcurrencyToken("v1"),
        };
    }

    private sealed class MutableEntity
    {
        public Guid Id { get; set; }

        public Guid TenantId { get; set; }

        public bool IsDeleted { get; set; }

        public DateTimeOffset? DeletedAtUtc { get; set; }

        public ConcurrencyToken Token { get; set; } = new("v1");

        public string Name { get; set; } = string.Empty;
    }
}
