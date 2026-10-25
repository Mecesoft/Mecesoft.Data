using Mecesoft.Data.Abstractions.Tenancy;

namespace Mecesoft.Data.Tests;

public sealed class TenantIdValidationTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant-a")]
    [InlineData("Acme Corp")]
    [InlineData("I")]
    [InlineData("ı")]
    [InlineData("İ")]
    public void CanonicalStringTenantIdsAreAccepted(string tenantId)
    {
        Assert.True(TenantIdValidation.IsCanonical(tenantId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\u00A0")]
    [InlineData(" tenant")]
    [InlineData("tenant ")]
    [InlineData("\ttenant")]
    [InlineData("tenant\n")]
    [InlineData("\u00A0tenant")]
    [InlineData("tenant\u00A0")]
    public void NonCanonicalStringTenantIdsAreRejected(string tenantId)
    {
        Assert.False(TenantIdValidation.IsCanonical(tenantId));
    }

    [Fact]
    public void NullIsRejected()
    {
        Assert.False(TenantIdValidation.IsCanonical(null));
    }

    [Fact]
    public void MaximumLengthBoundaryIsEnforced()
    {
        Assert.True(
            TenantIdValidation.IsCanonical(
                new string('a', TenantIdValidation.MaxStringLength)));
        Assert.False(
            TenantIdValidation.IsCanonical(
                new string('a', TenantIdValidation.MaxStringLength + 1)));
    }

    [Fact]
    public void ValidationIsOrdinalAndDoesNotNormalize()
    {
        const string first = "I";
        const string second = "ı";

        Assert.True(TenantIdValidation.IsCanonical(first));
        Assert.True(TenantIdValidation.IsCanonical(second));
        Assert.NotEqual(first, second);
    }
}
