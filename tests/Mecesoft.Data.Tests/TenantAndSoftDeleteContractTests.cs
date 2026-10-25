using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.Tests;

public sealed class TenantAndSoftDeleteContractTests
{
    [Fact]
    public async Task MissingTenantResolutionIsRepresentedAsFailure()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver = new MissingTenantResolver();

        DataResult<TenantScope<Guid>> result = await resolver.ResolveRequiredScopeAsync(TenantOperation.Read);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantUnavailableFailure>(result.Failure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void TenantMetadataDoesNotImplementAccessResolver()
    {
        var entity = new TenantEntity(Guid.NewGuid());

        Assert.IsAssignableFrom<ITenantEntity<Guid>>(entity);
        Assert.False(typeof(ITenantAccessResolver<TenantEntity, Guid>).IsAssignableFrom(entity.GetType()));
    }

    [Fact]
    public void TenantResolverDoesNotExposeCoreResolutionBypass()
    {
        Assert.Null(
            typeof(ITenantAccessResolver<TenantEntity, Guid>)
                .GetMethod("ResolveScopeCoreAsync"));
    }

    [Fact]
    public void MissingOrConstantTenantSelectorFailsClosed()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver = new InvalidSelectorResolver();

        DataResult<bool> result = resolver.ValidateConfiguration();

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public async Task ResolutionFailsClosedWhenConfigurationIsInvalid()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver = new InvalidSelectorResolver();

        DataResult<TenantScope<Guid>> result = await resolver.ResolveRequiredScopeAsync(TenantOperation.Read);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public void ValidTenantSelectorPassesConfigurationValidation()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver =
            new SuccessfulTenantResolver(Guid.NewGuid(), TenantOperation.Read);

        DataResult<bool> result = resolver.ValidateConfiguration();

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public void ThrowingSelectorFailsClosed()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver = new ThrowingSelectorResolver();

        DataResult<bool> result = resolver.ValidateConfiguration();

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public async Task DefaultScopeCheckRejectsDifferentTenant()
    {
        Guid tenantId = Guid.NewGuid();
        ITenantAccessResolver<TenantEntity, Guid> resolver =
            new SuccessfulTenantResolver(tenantId, TenantOperation.Read);
        var inScope = new TenantEntity(tenantId);
        var outOfScope = new TenantEntity(Guid.NewGuid());

        DataResult<bool> inScopeResult = await resolver.IsInScopeAsync(inScope, TenantOperation.Read);
        DataResult<bool> outOfScopeResult = await resolver.IsInScopeAsync(outOfScope, TenantOperation.Read);

        Assert.True(inScopeResult.IsSuccess);
        Assert.True(inScopeResult.Value);
        Assert.True(outOfScopeResult.IsSuccess);
        Assert.False(outOfScopeResult.Value);
    }

    [Fact]
    public async Task ScopeResolvedForAnotherOperationFailsClosed()
    {
        Guid tenantId = Guid.NewGuid();
        ITenantAccessResolver<TenantEntity, Guid> resolver =
            new SuccessfulTenantResolver(tenantId, TenantOperation.Read);
        var entity = new TenantEntity(tenantId);

        DataResult<bool> result = await resolver.IsInScopeAsync(entity, TenantOperation.Update);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public async Task GraphValidationCanAcceptOrRejectOwnership()
    {
        Guid tenantId = Guid.NewGuid();
        var resolver = new SuccessfulTenantResolver(tenantId, TenantOperation.Update);

        DataResult<bool> inScope = await resolver.IsGraphInScopeAsync(
            new TenantEntity(tenantId),
            TenantOperation.Update);
        DataResult<bool> outOfScope = await resolver.IsGraphInScopeAsync(
            new TenantEntity(Guid.NewGuid()),
            TenantOperation.Update);

        Assert.True(inScope.IsSuccess);
        Assert.True(inScope.Value);
        Assert.True(outOfScope.IsSuccess);
        Assert.False(outOfScope.Value);
    }

    [Fact]
    public async Task GraphValidationPropagatesFailClosedFailure()
    {
        ITenantAccessResolver<TenantEntity, Guid> resolver = new GraphRejectingTenantResolver();

        DataResult<bool> result = await resolver.IsGraphInScopeAsync(
            new TenantEntity(Guid.NewGuid()),
            TenantOperation.Update);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantOwnershipFailure>(result.Failure);
    }

    [Fact]
    public async Task SoftDeleteAuthorizationEchoesOperationAndDoesNotExposeAccessType()
    {
        var entity = new SoftDeleteEntity
        {
            TenantId = Guid.NewGuid(),
            IsDeleted = true,
        };
        var context = new SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid>(
            SoftDeleteOperation.Restore,
            entity,
            Guid.NewGuid(),
            entity.TenantId);
        var authorizer = new EchoSoftDeleteAuthorizer();

        DataResult<SoftDeleteOperation> result = await authorizer.AuthorizeAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(SoftDeleteOperation.Restore, result.Value);
        Assert.Equal(entity, context.Entity);
        Assert.Equal(entity.TenantId, context.TenantId);
        Assert.DoesNotContain(
            typeof(ISoftDeleteAuthorizer<,,>).Assembly.GetExportedTypes(),
            type => type.Name == "SoftDeleteAccess");
    }

    [Fact]
    public void SoftDeleteAuthorizationContextValidatesResourceData()
    {
        var entity = new SoftDeleteEntity
        {
            TenantId = Guid.NewGuid(),
        };
        var key = Guid.NewGuid();

        Assert.Throws<ArgumentNullException>(
            () => new SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid>(
                SoftDeleteOperation.Restore,
                null!,
                key,
                entity.TenantId));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid>(
                (SoftDeleteOperation)999,
                entity,
                key,
                entity.TenantId));
        Assert.Throws<ArgumentException>(
            () => new SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid>(
                SoftDeleteOperation.Restore,
                entity,
                key,
                Guid.NewGuid()));

        var context = new SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid>(
            SoftDeleteOperation.Restore,
            entity,
            key,
            entity.TenantId);

        Assert.Same(entity, context.Entity);
        Assert.Equal(key, context.Key);
        Assert.Equal(entity.TenantId, context.TenantId);
        Assert.Equal(SoftDeleteOperation.Restore, context.Operation);
    }

    private sealed record TenantEntity(Guid TenantId) : ITenantEntity<Guid>;

    private sealed class MissingTenantResolver : ITenantAccessResolver<TenantEntity, Guid>
    {
        public Expression<Func<TenantEntity, Guid>> TenantIdSelector => entity => entity.TenantId;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Failed<TenantScope<Guid>>(
                    new TenantUnavailableFailure("A required tenant could not be resolved.")));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class InvalidSelectorResolver : ITenantAccessResolver<TenantEntity, Guid>
    {
        public Expression<Func<TenantEntity, Guid>> TenantIdSelector => _ => Guid.Empty;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            DataResult<bool> configuration = TenantContractGuard.ValidateSelector(TenantIdSelector);
            if (configuration.IsFailure)
            {
                return ValueTask.FromResult(
                    DataResult.Failed<TenantScope<Guid>>(configuration.Failure!));
            }

            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(Guid.NewGuid(), operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class SuccessfulTenantResolver(Guid tenantId, TenantOperation scopeOperation)
        : ITenantAccessResolver<TenantEntity, Guid>
    {
        public Guid TenantId { get; } = tenantId;

        public Expression<Func<TenantEntity, Guid>> TenantIdSelector => entity => entity.TenantId;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(TenantId, scopeOperation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(root.TenantId == TenantId));
        }
    }

    private sealed class ThrowingSelectorResolver : ITenantAccessResolver<TenantEntity, Guid>
    {
        public Expression<Func<TenantEntity, Guid>> TenantIdSelector
            => throw new InvalidOperationException("The selector is not configured.");

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(Guid.NewGuid(), operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(true));
        }
    }

    private sealed class GraphRejectingTenantResolver : ITenantAccessResolver<TenantEntity, Guid>
    {
        public Expression<Func<TenantEntity, Guid>> TenantIdSelector => entity => entity.TenantId;

        public ValueTask<DataResult<TenantScope<Guid>>> ResolveRequiredScopeAsync(
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Succeeded(new TenantScope<Guid>(Guid.NewGuid(), operation)));
        }

        public ValueTask<DataResult<bool>> IsGraphInScopeAsync(
            TenantEntity root,
            TenantOperation operation,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                DataResult.Failed<bool>(
                    new TenantOwnershipFailure("A related entity is outside the tenant scope.")));
        }
    }

    private sealed class SoftDeleteEntity : ISoftDeletable, ITenantEntity<Guid>
    {
        public Guid TenantId { get; init; }

        public bool IsDeleted { get; init; }

        public DateTimeOffset? DeletedAtUtc { get; init; }
    }

    private sealed class EchoSoftDeleteAuthorizer : ISoftDeleteAuthorizer<SoftDeleteEntity, Guid, Guid>
    {
        public ValueTask<DataResult<SoftDeleteOperation>> AuthorizeAsync(
            SoftDeleteAuthorizationContext<SoftDeleteEntity, Guid, Guid> context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(DataResult.Succeeded(context.Operation));
        }
    }
}
