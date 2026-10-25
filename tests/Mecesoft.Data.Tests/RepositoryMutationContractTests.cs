using System.Reflection;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Tests;

public sealed class RepositoryMutationContractTests
{
    [Fact]
    public async Task MutationCanReturnTypedValidationFailure()
    {
        EntityMutation<MutableEntity> mutation = static (_, _) =>
            ValueTask.FromResult(
                DataResult.Failed<EntityMutationResult>(
                    new ValidationFailure("The entity is not valid.")));

        DataResult<EntityMutationResult> result = await mutation(
            new MutableEntity(),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.IsType<ValidationFailure>(result.Failure);
    }

    [Fact]
    public void MutationSuccessUsesCompletedMarker()
    {
        Assert.Same(EntityMutationResult.Completed, EntityMutationResult.Completed);
    }

    [Fact]
    public void UpdateReturnsEntityAndCurrentConcurrencyTokenShape()
    {
        MethodInfo update = typeof(IRepository<,>).GetMethod("UpdateAsync")!;
        Type valueTask = update.ReturnType;
        Assert.True(valueTask.IsGenericType);
        Assert.Equal(typeof(ValueTask<>), valueTask.GetGenericTypeDefinition());

        Type dataResult = valueTask.GetGenericArguments()[0];
        Assert.True(dataResult.IsGenericType);
        Assert.Equal(typeof(DataResult<>), dataResult.GetGenericTypeDefinition());

        Type updateResult = dataResult.GetGenericArguments()[0];
        Assert.True(updateResult.IsGenericType);
        Assert.Equal(typeof(EntityUpdateResult<>), updateResult.GetGenericTypeDefinition());
    }

    [Fact]
    public void MutationDelegateReturnsTypedResult()
    {
        MethodInfo invoke = typeof(EntityMutation<>).GetMethod("Invoke")!;
        Type valueTask = invoke.ReturnType;
        Assert.True(valueTask.IsGenericType);
        Assert.Equal(typeof(ValueTask<>), valueTask.GetGenericTypeDefinition());

        Type dataResult = valueTask.GetGenericArguments()[0];
        Assert.True(dataResult.IsGenericType);
        Assert.Equal(typeof(DataResult<>), dataResult.GetGenericTypeDefinition());
        Assert.Equal(typeof(EntityMutationResult), dataResult.GetGenericArguments()[0]);
    }

    [Fact]
    public void EntityUpdateResultRequiresEntityAndToken()
    {
        var entity = new MutableEntity();
        var token = new ConcurrencyToken("v2");

        Assert.Throws<ArgumentNullException>(
            () => new EntityUpdateResult<MutableEntity>(null!, token));
        Assert.Throws<ArgumentNullException>(
            () => new EntityUpdateResult<MutableEntity>(entity, null!));

        var result = new EntityUpdateResult<MutableEntity>(entity, token);
        Assert.Same(entity, result.Entity);
        Assert.Same(token, result.ConcurrencyToken);
    }

    private sealed class MutableEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
