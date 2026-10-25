using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.Tests;

public sealed class TenantConfigurationGuardTests
{
    [Fact]
    public void ValidMemberSelectorIsAccepted()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(
            entity => entity.TenantId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public void NestedMemberSelectorIsAccepted()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<NestedTenantEntity, Guid>(
            entity => entity.Tenant.Id);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ConvertedParameterMemberSelectorIsAccepted()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(
            entity => ((ITenantEntity<Guid>)entity).TenantId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void MissingSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(null);

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public void ConstantSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<StringTenantEntity, string>(
            _ => "tenant-a");

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public void StaticMemberSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(
            _ => Guid.Empty);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void MethodCallSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(
            entity => CreateTenantId(entity));

        Assert.True(result.IsFailure);
        Assert.IsType<TenantConfigurationFailure>(result.Failure);
    }

    [Fact]
    public void ParameterSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<SelfTenantEntity, SelfTenantEntity>(
            entity => entity);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ConditionalSelectorFailsClosed()
    {
        DataResult<bool> result = TenantContractGuard.ValidateSelector<TenantEntity, Guid>(
            entity => entity.TenantId == Guid.Empty ? Guid.NewGuid() : entity.TenantId);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void TenantScopeCarriesOperationAndRejectsInvalidValues()
    {
        Guid tenantId = Guid.NewGuid();
        var scope = new TenantScope<Guid>(tenantId, TenantOperation.Restore);

        Assert.Equal(tenantId, scope.TenantId);
        Assert.Equal(TenantOperation.Restore, scope.Operation);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TenantScope<Guid>(tenantId, (TenantOperation)999));
        Assert.Throws<ArgumentNullException>(
            () => new TenantScope<string>(null!, TenantOperation.Read));
    }

    private static Guid CreateTenantId(TenantEntity entity)
    {
        return entity.TenantId;
    }

    private sealed record TenantEntity(Guid TenantId) : ITenantEntity<Guid>;

    private sealed record StringTenantEntity(string TenantId) : ITenantEntity<string>;

    private sealed class SelfTenantEntity : ITenantEntity<SelfTenantEntity>
    {
        public SelfTenantEntity TenantId => this;
    }

    private sealed record NestedTenantEntity(TenantInfo Tenant) : ITenantEntity<Guid>
    {
        public Guid TenantId => Tenant.Id;
    }

    private sealed record TenantInfo(Guid Id);
}
