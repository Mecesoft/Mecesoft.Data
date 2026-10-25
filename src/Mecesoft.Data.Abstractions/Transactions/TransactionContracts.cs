using Mecesoft.Data.Abstractions.Results;

namespace Mecesoft.Data.Abstractions.Transactions;

/// <summary>
/// Specifies a provider-neutral transaction isolation preference.
/// </summary>
public enum DataIsolationLevel
{
    /// <summary>
    /// Uses the adapter or database default.
    /// </summary>
    ProviderDefault,

    /// <summary>
    /// Allows dirty reads where the provider supports them.
    /// </summary>
    ReadUncommitted,

    /// <summary>
    /// Prevents dirty reads.
    /// </summary>
    ReadCommitted,

    /// <summary>
    /// Prevents values read in a transaction from changing before completion.
    /// </summary>
    RepeatableRead,

    /// <summary>
    /// Requests serializable execution.
    /// </summary>
    Serializable,

    /// <summary>
    /// Requests snapshot isolation where supported and enabled.
    /// </summary>
    Snapshot,
}

/// <summary>
/// Represents provider-neutral transaction options.
/// </summary>
/// <param name="IsolationLevel">The requested isolation preference.</param>
public sealed record TransactionOptions(DataIsolationLevel IsolationLevel = DataIsolationLevel.ProviderDefault);

/// <summary>
/// Identifies the lifecycle state of a transaction.
/// </summary>
public enum DataTransactionState
{
    /// <summary>
    /// The transaction is active.
    /// </summary>
    Active,

    /// <summary>
    /// The transaction was committed.
    /// </summary>
    Committed,

    /// <summary>
    /// The transaction was rolled back.
    /// </summary>
    RolledBack,

    /// <summary>
    /// The transaction was disposed before a successful commit.
    /// </summary>
    Disposed,

    /// <summary>
    /// The commit or rollback outcome is unknown, typically because cancellation or a transport
    /// failure occurred after the command was sent. The caller must not retry blindly.
    /// </summary>
    Indeterminate,
}

/// <summary>
/// Describes a completed rollback operation.
/// </summary>
/// <param name="State">The resulting transaction state.</param>
public sealed record TransactionCompletion(DataTransactionState State);

/// <summary>
/// Describes a successful unit-of-work commit.
/// </summary>
/// <param name="AffectedEntries">
/// The adapter-reported number of entries affected by write operations in this transaction. The
/// value is diagnostic and must not be used as a security or consistency guarantee.
/// </param>
public sealed record UnitOfWorkCommit(long AffectedEntries);

/// <summary>
/// Represents an adapter-owned transaction state handle.
/// </summary>
/// <remarks>
/// Commit and rollback are owned exclusively by <see cref="IUnitOfWork"/>. This handle exposes state
/// only; it cannot commit, roll back, or independently dispose the transaction. The unit of work owns
/// the transaction lifetime and must roll back an active transaction when disposed.
/// </remarks>
public interface IDataTransaction
{
    /// <summary>
    /// Gets the current transaction state.
    /// </summary>
    DataTransactionState State { get; }
}

/// <summary>
/// Runs application-specific work inside the unit-of-work transaction before the commit is dispatched.
/// </summary>
/// <remarks>
/// This is the supported extension point for audit and outbox adapters. Implementations must write
/// through the same unit of work/transaction and must not call <see cref="IUnitOfWork.CommitAsync"/>
/// or <see cref="IUnitOfWork.RollbackAsync"/>. A failed result aborts the commit. The abstraction
/// cannot enforce these rules at runtime.
/// </remarks>
public interface IUnitOfWorkCommitParticipant
{
    /// <summary>
    /// Prepares work that must be persisted atomically with the unit of work.
    /// </summary>
    /// <param name="unitOfWork">The owning unit of work. The participant must not commit or roll back it.</param>
    /// <param name="cancellationToken">The token used to cancel preparation.</param>
    /// <returns>
    /// Success with <see langword="true"/> when the participant is ready; otherwise, a typed failure.
    /// A successful result with <see langword="false"/> is also treated as a denial.
    /// </returns>
    ValueTask<DataResult<bool>> BeforeCommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines the single commit and transaction boundary shared by repositories in one scope.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one active transaction per unit of work. <see cref="IUnitOfWork"/> is the only
/// owner of commit and rollback; <see cref="IDataTransaction"/> is a state handle only.
/// </para>
/// <para>
/// Repository write operations must run inside an active transaction. They execute within that
/// transaction but never commit. Only <see cref="CommitAsync"/> is durable. On any failure the
/// adapter must roll back and must not expose uncommitted results as successful.
/// </para>
/// <para>
/// For audit/outbox integration, <see cref="CommitParticipants"/> run in order before the commit is
/// dispatched. A participant failure aborts the commit and requires rollback.
/// </para>
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether this unit of work owns a transaction in the
    /// <see cref="DataTransactionState.Active"/> state.
    /// </summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Gets the current transaction state handle, including an indeterminate transaction that must be
    /// inspected, or <see langword="null"/> when no transaction exists.
    /// </summary>
    IDataTransaction? CurrentTransaction { get; }

    /// <summary>
    /// Gets the ordered commit participants invoked before the commit is dispatched.
    /// </summary>
    IReadOnlyList<IUnitOfWorkCommitParticipant> CommitParticipants { get; }

    /// <summary>
    /// Begins a new transaction owned by this unit of work.
    /// </summary>
    /// <param name="options">The provider-neutral transaction options.</param>
    /// <param name="cancellationToken">The token used to cancel transaction creation.</param>
    /// <returns>
    /// The transaction handle, a <see cref="TransactionStateFailure"/> when a transaction is already
    /// active, or a typed failure for unsupported options.
    /// </returns>
    /// <remarks>
    /// Nested transactions are not part of this contract. The adapter must fail rather than create a
    /// second transaction when <see cref="HasActiveTransaction"/> is already <see langword="true"/>.
    /// </remarks>
    ValueTask<DataResult<IDataTransaction>> BeginTransactionAsync(
        TransactionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs commit participants and commits the active transaction.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel work before the commit is dispatched.</param>
    /// <returns>The commit summary or a typed failure.</returns>
    /// <remarks>
    /// <para>
    /// This is the only commit path. Implementations must:
    /// </para>
    /// <list type="number">
    /// <item><description>return <see cref="TransactionRequiredFailure"/> when no transaction is active;</description></item>
    /// <item><description>return <see cref="TransactionStateFailure"/> for an invalid state;</description></item>
    /// <item><description>invoke <see cref="CommitParticipants"/> in order and abort on the first failure;</description></item>
    /// <item><description>observe cancellation only before the commit is dispatched;</description></item>
    /// <item><description>return <see cref="IndeterminateCommitFailure"/> and mark the transaction <see cref="DataTransactionState.Indeterminate"/> when the outcome is unknown.</description></item>
    /// </list>
    /// <para>
    /// If cancellation is observed before dispatch, the transaction remains active and the caller may
    /// roll back. After a failure, the adapter must leave the transaction in a consistent state and
    /// must not report a successful commit.
    /// </para>
    /// </remarks>
    ValueTask<DataResult<UnitOfWorkCommit>> CommitAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back the active transaction.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel rollback before it is dispatched.</param>
    /// <returns>The rollback completion or a typed failure.</returns>
    /// <remarks>
    /// A missing transaction returns <see cref="TransactionRequiredFailure"/>. An indeterminate
    /// transaction returns <see cref="IndeterminateCommitFailure"/> because blind rollback is unsafe.
    /// If cancellation is observed before rollback is dispatched, the transaction remains active.
    /// If cancellation is observed after rollback is dispatched and the outcome is unknown, return
    /// <see cref="IndeterminateCommitFailure"/> and mark the transaction
    /// <see cref="DataTransactionState.Indeterminate"/>.
    /// </remarks>
    ValueTask<DataResult<TransactionCompletion>> RollbackAsync(
        CancellationToken cancellationToken = default);
}
