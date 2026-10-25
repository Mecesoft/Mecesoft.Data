using Mecesoft.Data.Abstractions.Transactions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mecesoft.Data.EntityFrameworkCore.Transactions;

internal sealed class EfDataTransaction : IDataTransaction
{
    public EfDataTransaction(IDbContextTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        Transaction = transaction;
    }

    public DataTransactionState State { get; private set; } = DataTransactionState.Active;

    internal IDbContextTransaction Transaction { get; }

    internal void MarkCommitted() => State = DataTransactionState.Committed;

    internal void MarkRolledBack() => State = DataTransactionState.RolledBack;

    internal void MarkIndeterminate() => State = DataTransactionState.Indeterminate;
}
