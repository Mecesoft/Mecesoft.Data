using System.Reflection;
using Mecesoft.Data.Abstractions.Repositories;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Transactions;

namespace Mecesoft.Data.Tests;

public sealed class TransactionContractTests
{
    [Fact]
    public void DataTransactionExposesStateOnly()
    {
        Assert.NotNull(typeof(IDataTransaction).GetProperty("State"));
        Assert.Null(typeof(IDataTransaction).GetMethod("CommitAsync"));
        Assert.Null(typeof(IDataTransaction).GetMethod("RollbackAsync"));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IDataTransaction)));
    }

    [Fact]
    public void UnitOfWorkOwnsCommitRollbackAndParticipants()
    {
        Assert.NotNull(typeof(IUnitOfWork).GetMethod("BeginTransactionAsync"));
        Assert.NotNull(typeof(IUnitOfWork).GetMethod("CommitAsync"));
        Assert.NotNull(typeof(IUnitOfWork).GetMethod("RollbackAsync"));
        Assert.NotNull(typeof(IUnitOfWork).GetProperty("CurrentTransaction"));
        Assert.NotNull(typeof(IUnitOfWork).GetProperty("HasActiveTransaction"));

        PropertyInfo participants = typeof(IUnitOfWork).GetProperty("CommitParticipants")!;
        Assert.True(participants.PropertyType.IsGenericType);
        Assert.Equal(typeof(IReadOnlyList<>), participants.PropertyType.GetGenericTypeDefinition());
        Assert.Equal(
            typeof(IUnitOfWorkCommitParticipant),
            participants.PropertyType.GetGenericArguments()[0]);
    }

    [Fact]
    public void RepositoryExposesUnitOfWorkBoundary()
    {
        Assert.Equal(
            typeof(IUnitOfWork),
            typeof(IRepository<,>).GetProperty("UnitOfWork")!.PropertyType);
        Assert.Equal(
            typeof(IUnitOfWork),
            typeof(ISoftDeleteRepository<,>).GetProperty("UnitOfWork")!.PropertyType);
    }

    [Fact]
    public void CommitParticipantIsAsyncAndReturnsTypedResult()
    {
        MethodInfo method = typeof(IUnitOfWorkCommitParticipant).GetMethod("BeforeCommitAsync")!;
        Type resultType = method.ReturnType.GetGenericArguments()[0];

        Assert.Contains(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(IUnitOfWork));
        Assert.Contains(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken));
        Assert.True(resultType.IsGenericType);
        Assert.Equal(typeof(DataResult<>), resultType.GetGenericTypeDefinition());
        Assert.Equal(typeof(bool), resultType.GetGenericArguments()[0]);
    }

    [Fact]
    public void IndeterminateStateAndTransactionFailuresAreStable()
    {
        Assert.True(Enum.IsDefined(DataTransactionState.Indeterminate));
        Assert.Equal(
            "data.transaction_required",
            new TransactionRequiredFailure("write").Code);
        Assert.Equal(
            "data.transaction_state_invalid",
            new TransactionStateFailure(DataTransactionState.Active, "invalid").Code);
        Assert.Equal(
            "data.transaction_indeterminate",
            new IndeterminateCommitFailure("unknown").Code);
    }
}
