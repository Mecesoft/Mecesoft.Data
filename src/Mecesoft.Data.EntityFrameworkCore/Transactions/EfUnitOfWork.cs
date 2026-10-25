using System.Data;
using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mecesoft.Data.EntityFrameworkCore.Transactions;

/// <summary>
/// EF Core implementation of the single-owner unit-of-work transaction boundary.
/// </summary>
/// <typeparam name="TContext">The DbContext type.</typeparam>
/// <remarks>
/// The unit of work owns commit and rollback. Transaction handles expose state only. Repository write
/// operations are expected to execute inside the active transaction and must not commit.
/// </remarks>
public sealed class EfUnitOfWork<TContext> : IUnitOfWork
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly IReadOnlyList<IUnitOfWorkCommitParticipant> _commitParticipants;
    private readonly Func<CancellationToken, Task<IDbContextTransaction>>? _transactionFactory;
    private EfDataTransaction? _current;
    private bool _commitInProgress;

    /// <summary>
    /// Initializes a new EF Core unit of work.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    /// <param name="commitParticipants">Ordered commit participants.</param>
    public EfUnitOfWork(
        TContext context,
        IEnumerable<IUnitOfWorkCommitParticipant>? commitParticipants = null)
        : this(context, commitParticipants, transactionFactory: null)
    {
    }

    internal EfUnitOfWork(
        TContext context,
        IEnumerable<IUnitOfWorkCommitParticipant>? commitParticipants,
        Func<CancellationToken, Task<IDbContextTransaction>>? transactionFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _commitParticipants = commitParticipants?.ToArray() ?? [];
        _transactionFactory = transactionFactory;
    }

    /// <inheritdoc />
    public bool HasActiveTransaction => _current?.State == DataTransactionState.Active;

    /// <inheritdoc />
    public IDataTransaction? CurrentTransaction => _current;

    /// <inheritdoc />
    public IReadOnlyList<IUnitOfWorkCommitParticipant> CommitParticipants => _commitParticipants;

    /// <inheritdoc />
    public async ValueTask<DataResult<IDataTransaction>> BeginTransactionAsync(
        TransactionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_commitInProgress)
        {
            return DataResult.Failed<IDataTransaction>(
                new TransactionStateFailure(
                    DataTransactionState.Active,
                    "A commit is in progress; a new transaction cannot be started."));
        }

        if (_current is not null)
        {
            if (_current.State is DataTransactionState.Active or DataTransactionState.Indeterminate)
            {
                return DataResult.Failed<IDataTransaction>(
                    new TransactionStateFailure(
                        _current.State,
                        "A transaction is already active or indeterminate."));
            }

            await _current.Transaction.DisposeAsync().ConfigureAwait(false);
            _current = null;
        }

        if (_context.Database.CurrentTransaction is not null)
        {
            return DataResult.Failed<IDataTransaction>(
                new TransactionStateFailure(
                    DataTransactionState.Active,
                    "The DbContext already has a transaction that is not owned by this unit of work."));
        }

        try
        {
            IDbContextTransaction transaction = _transactionFactory is not null
                ? await _transactionFactory(cancellationToken).ConfigureAwait(false)
                : options.IsolationLevel == DataIsolationLevel.ProviderDefault
                    ? await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                    : await _context.Database
                        .BeginTransactionAsync(MapIsolationLevel(options.IsolationLevel), cancellationToken)
                        .ConfigureAwait(false);

            _current = new EfDataTransaction(transaction);
            return DataResult.Succeeded<IDataTransaction>(_current);
        }
        catch (OperationCanceledException)
        {
            return DataResult.Failed<IDataTransaction>(
                new ValidationFailure("Transaction creation was cancelled."));
        }
        catch (Exception)
        {
            return DataResult.Failed<IDataTransaction>(
                new ValidationFailure(
                    "The provider could not create the requested transaction."));
        }
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<UnitOfWorkCommit>> CommitAsync(
        CancellationToken cancellationToken = default)
    {
        if (_commitInProgress)
        {
            return DataResult.Failed<UnitOfWorkCommit>(
                new TransactionStateFailure(
                    DataTransactionState.Active,
                    "A commit is already in progress."));
        }

        EfDataTransaction? current = _current;
        if (current is null)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<UnitOfWorkCommit>(
                new TransactionRequiredFailure("commit"));
        }

        if (current.State == DataTransactionState.Indeterminate)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<UnitOfWorkCommit>(
                new IndeterminateCommitFailure(
                    "The transaction outcome is indeterminate and cannot be committed again."));
        }

        if (current.State != DataTransactionState.Active)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<UnitOfWorkCommit>(
                new TransactionStateFailure(
                    current.State,
                    "The transaction is not active."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<UnitOfWorkCommit>(
                new ValidationFailure("The commit was cancelled before dispatch."));
        }

        MecesoftDbContext? mecesoftContext = _context as MecesoftDbContext;
        mecesoftContext?.FreezeTenantScope();
        _commitInProgress = true;
        try
        {
            return await CommitCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                mecesoftContext?.UnfreezeTenantScope();
            }
            finally
            {
                mecesoftContext?.ClearAuthorizationGrants();
                _commitInProgress = false;
            }
        }
    }

    private async ValueTask<DataResult<UnitOfWorkCommit>> CommitCoreAsync(
        EfDataTransaction current,
        CancellationToken cancellationToken)
    {
        foreach (IUnitOfWorkCommitParticipant participant in _commitParticipants)
        {
            DataResult<bool> participantResult;
            try
            {
                participantResult = await participant
                    .BeforeCommitAsync(this, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return DataResult.Failed<UnitOfWorkCommit>(
                    new ValidationFailure("The commit was cancelled before dispatch."));
            }
            catch (Exception)
            {
                DataResult<TransactionCompletion> failedRollback =
                    await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return failedRollback.IsFailure
                    ? DataResult.Failed<UnitOfWorkCommit>(failedRollback.Failure!)
                    : DataResult.Failed<UnitOfWorkCommit>(
                        new PersistenceFailure("A commit participant failed."));
            }

            if (participantResult.IsFailure)
            {
                DataResult<TransactionCompletion> rollback =
                    await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return rollback.IsFailure
                    ? DataResult.Failed<UnitOfWorkCommit>(rollback.Failure!)
                    : DataResult.Failed<UnitOfWorkCommit>(participantResult.Failure!);
            }

            if (!participantResult.Value)
            {
                DataResult<TransactionCompletion> rollback =
                    await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                return rollback.IsFailure
                    ? DataResult.Failed<UnitOfWorkCommit>(rollback.Failure!)
                    : DataResult.Failed<UnitOfWorkCommit>(
                        new ValidationFailure("A commit participant denied the commit."));
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return DataResult.Failed<UnitOfWorkCommit>(
                new ValidationFailure("The commit was cancelled before dispatch."));
        }

        int affectedEntries;
        try
        {
            affectedEntries = await _context
                .SaveChangesAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SaveChangesSecurityException exception)
        {
            DataResult<TransactionCompletion> rollback =
                await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            return rollback.IsFailure
                ? DataResult.Failed<UnitOfWorkCommit>(rollback.Failure!)
                : DataResult.Failed<UnitOfWorkCommit>(exception.Failure);
        }
        catch (Exception)
        {
            DataResult<TransactionCompletion> rollback =
                await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            return rollback.IsFailure
                ? DataResult.Failed<UnitOfWorkCommit>(rollback.Failure!)
                : DataResult.Failed<UnitOfWorkCommit>(
                    new PersistenceFailure("The database update failed before commit."));
        }

        try
        {
            await current.Transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            current.MarkCommitted();
            return DataResult.Succeeded(new UnitOfWorkCommit(affectedEntries));
        }
        catch (Exception)
        {
            current.MarkIndeterminate();
            return DataResult.Failed<UnitOfWorkCommit>(
                new IndeterminateCommitFailure("The commit outcome is unknown."));
        }
        finally
        {
            try
            {
                await current.Transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The state handle already reflects the committed/indeterminate outcome.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<DataResult<TransactionCompletion>> RollbackAsync(
        CancellationToken cancellationToken = default)
    {
        if (_commitInProgress)
        {
            return DataResult.Failed<TransactionCompletion>(
                new TransactionStateFailure(
                    DataTransactionState.Active,
                    "A rollback cannot run while a commit is in progress."));
        }

        EfDataTransaction? current = _current;
        if (current is null)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new TransactionRequiredFailure("rollback"));
        }

        if (current.State == DataTransactionState.Indeterminate)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new IndeterminateCommitFailure(
                    "A transaction with an indeterminate outcome cannot be rolled back blindly."));
        }

        if (current.State != DataTransactionState.Active)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new TransactionStateFailure(
                    current.State,
                    "The transaction is not active."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new ValidationFailure("Rollback was cancelled before dispatch."));
        }

        return await RollbackCurrentAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_commitInProgress)
        {
            throw new InvalidOperationException(
                "The unit of work cannot be disposed while a commit is in progress.");
        }

        EfDataTransaction? current = _current;
        if (current is null)
        {
            ClearContextAuthorizationGrants();
            return;
        }

        if (current.State == DataTransactionState.Active)
        {
            try
            {
                await current.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                current.MarkRolledBack();
                _context.ChangeTracker.Clear();
            }
            catch (Exception)
            {
                current.MarkIndeterminate();
            }
        }

        try
        {
            await current.Transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the terminal state. An indeterminate outcome must remain inspectable.
        }

        ClearContextAuthorizationGrants();
        if (current.State != DataTransactionState.Indeterminate)
        {
            _current = null;
        }
    }

    private async ValueTask<DataResult<TransactionCompletion>> RollbackCurrentAsync(
        CancellationToken cancellationToken)
    {
        EfDataTransaction? current = _current;
        if (current is null)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new TransactionRequiredFailure("rollback"));
        }

        if (current.State == DataTransactionState.Indeterminate)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new IndeterminateCommitFailure(
                    "A transaction with an indeterminate outcome cannot be rolled back blindly."));
        }

        if (current.State != DataTransactionState.Active)
        {
            ClearContextAuthorizationGrants();
            return DataResult.Failed<TransactionCompletion>(
                new TransactionStateFailure(
                    current.State,
                    "The transaction is not active."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return DataResult.Failed<TransactionCompletion>(
                new ValidationFailure("Rollback was cancelled before dispatch."));
        }

        try
        {
            await current.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            current.MarkRolledBack();
            _context.ChangeTracker.Clear();
            return DataResult.Succeeded(
                new TransactionCompletion(DataTransactionState.RolledBack));
        }
        catch (Exception)
        {
            current.MarkIndeterminate();
            return DataResult.Failed<TransactionCompletion>(
                new IndeterminateCommitFailure("The rollback outcome is unknown."));
        }
        finally
        {
            ClearContextAuthorizationGrants();
            try
            {
                await current.Transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the rollback outcome; a disposal failure does not change it.
            }
        }
    }

    private void ClearContextAuthorizationGrants()
    {
        if (_context is MecesoftDbContext mecesoftContext)
        {
            mecesoftContext.ClearAuthorizationGrants();
        }
    }

    private static IsolationLevel MapIsolationLevel(DataIsolationLevel isolationLevel)
    {
        return isolationLevel switch
        {
            DataIsolationLevel.ReadUncommitted => IsolationLevel.ReadUncommitted,
            DataIsolationLevel.ReadCommitted => IsolationLevel.ReadCommitted,
            DataIsolationLevel.RepeatableRead => IsolationLevel.RepeatableRead,
            DataIsolationLevel.Serializable => IsolationLevel.Serializable,
            DataIsolationLevel.Snapshot => IsolationLevel.Snapshot,
            _ => throw new ArgumentOutOfRangeException(
                nameof(isolationLevel),
                isolationLevel,
                "The isolation level is not supported."),
        };
    }
}
