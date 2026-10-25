using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Tests;

public sealed class ResultModelTests
{
    [Fact]
    public void SuccessfulResultContainsValueWithoutFailure()
    {
        DataResult<int> result = DataResult.Succeeded(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void FailedResultContainsTypedFailureAndRejectsValueAccess()
    {
        var failure = new NotFoundFailure<Guid>("Order", Guid.NewGuid());
        DataResult<string> result = DataResult.Failed<string>(failure);

        Assert.True(result.IsFailure);
        Assert.Same(failure, result.Failure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ConcurrencyTokenRejectsEmptyValues()
    {
        Assert.Throws<ArgumentException>(() => new ConcurrencyToken(" "));
        Assert.Equal("v1", new ConcurrencyToken("v1").Value);
    }

    [Fact]
    public void SecurityFailuresExposeStableCodes()
    {
        Assert.Equal(
            "data.tenant_configuration_invalid",
            new TenantConfigurationFailure("invalid").Code);
        Assert.Equal(
            "data.tenant_ownership_violation",
            new TenantOwnershipFailure("out of scope").Code);

        var invariantFailure = new MutationInvariantFailure("tenant_id", "changed");
        Assert.Equal("data.mutation_invariant_violation", invariantFailure.Code);
        Assert.Equal("tenant_id", invariantFailure.Invariant);
    }
}
