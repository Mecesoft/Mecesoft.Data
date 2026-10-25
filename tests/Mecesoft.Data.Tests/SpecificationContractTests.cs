using System.Linq.Expressions;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Specifications;

namespace Mecesoft.Data.Tests;

public sealed class SpecificationContractTests
{
    [Fact]
    public void PaginationRequiresAtLeastOneSort()
    {
        var specification = new TestSpecification([]);

        DataResult<bool> result = SpecificationContractGuard.ValidateForPagination(specification);

        Assert.True(result.IsFailure);
        Assert.IsType<UnstableOrderingFailure>(result.Failure);
    }

    [Fact]
    public void PaginationAcceptsAUsableSort()
    {
        ISortExpression<TestEntity>[] sorts =
        [
            new SortExpression<TestEntity, Guid>(
                entity => entity.Id,
                SortDirection.Ascending),
        ];
        var specification = new TestSpecification(sorts);

        DataResult<bool> result = SpecificationContractGuard.ValidateForPagination(specification);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public void PaginationRejectsMissingSortExpression()
    {
        var specification = new TestSpecification([null!]);

        DataResult<bool> result = SpecificationContractGuard.ValidateForPagination(specification);

        Assert.True(result.IsFailure);
        Assert.IsType<UnsupportedExpressionFailure>(result.Failure);
    }

    [Fact]
    public void NullSpecificationIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => SpecificationContractGuard.ValidateForPagination<TestEntity>(null!));
    }

    [Fact]
    public void UnsupportedExpressionFailureHasStableCode()
    {
        Assert.Equal(
            "data.unsupported_expression",
            new UnsupportedExpressionFailure("criteria", "unsupported").Code);
        Assert.Equal(
            "data.unstable_ordering",
            new UnstableOrderingFailure("missing sort").Code);
    }

    private sealed class TestSpecification(IReadOnlyList<ISortExpression<TestEntity>> sorts)
        : ISpecification<TestEntity>
    {
        public Expression<Func<TestEntity, bool>>? Criteria => null;

        public IReadOnlyList<ISortExpression<TestEntity>> Sorts { get; } = sorts;
    }

    private sealed class TestEntity
    {
        public Guid Id { get; init; }
    }
}
