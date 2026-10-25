using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mecesoft.Repository;

/// <summary>
/// Defines a DbContext-owning unit of work for general-purpose applications.
/// </summary>
/// <remarks>
/// <para>
/// This is the compatibility and general-purpose unit of work. The secure
/// <c>Mecesoft.Data.Abstractions.Transactions.IUnitOfWork</c> remains the transaction boundary used
/// by the secure repositories. Do not mix the two units of work on the same DbContext instance.
/// </para>
/// <para>
/// All repositories that share the same scoped unit of work also share its DbContext and its
/// tracked changes. <see cref="SaveChangesAsync"/> persists those changes without committing an
/// explicit transaction; EF Core wraps a unit of SaveChanges in its own transaction.
/// </para>
/// </remarks>
public interface IUnitOfWork : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether this unit of work owns an active explicit transaction.
    /// </summary>
    bool HasActiveTransaction { get; }

    /// <summary>
    /// Gets the active explicit transaction, or <see langword="null"/> when none exists.
    /// </summary>
    IDbContextTransaction? CurrentTransaction { get; }

    /// <summary>
    /// Saves all tracked changes.
    /// </summary>
    /// <returns>The number of affected entries.</returns>
    int SaveChanges();

    /// <summary>
    /// Saves all tracked changes asynchronously.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The number of affected entries.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins an explicit transaction owned by this unit of work.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when the transaction is active.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already active.</exception>
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits and disposes the active transaction.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the commit before dispatch.</param>
    /// <returns>A task that completes when the commit is durable.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active.</exception>
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back and disposes the active transaction.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the rollback before dispatch.</param>
    /// <returns>A task that completes when the rollback is complete.</returns>
    /// <exception cref="InvalidOperationException">No transaction is active.</exception>
    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs work inside a single retriable transaction and saves changes before committing.
    /// </summary>
    /// <param name="operation">The work to execute inside the transaction.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs work inside a single retriable transaction, saves changes, and returns a result.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="operation">The work to execute inside the transaction.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The operation result after the transaction is committed.</returns>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements a general-purpose unit of work over a DbContext.
/// </summary>
/// <typeparam name="TContext">The DbContext type.</typeparam>
/// <remarks>
/// The unit of work does not create, dispose, or own the DbContext. Register both types as scoped
/// services so every repository in one request shares the same instance.
/// </remarks>
public sealed class UnitOfWork<TContext> : IUnitOfWork
    where TContext : DbContext
{
    private readonly TContext _context;
    private IDbContextTransaction? _transaction;
    private bool _disposed;

    /// <summary>
    /// Initializes a new unit of work.
    /// </summary>
    /// <param name="context">The scoped DbContext.</param>
    public UnitOfWork(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public bool HasActiveTransaction => _transaction is not null;

    /// <inheritdoc />
    public IDbContextTransaction? CurrentTransaction => _transaction;

    /// <inheritdoc />
    public int SaveChanges()
    {
        ThrowIfDisposed();

        if (_transaction is not null || _context.Database.CurrentTransaction is not null)
        {
            return _context.SaveChanges();
        }

        return _context.Database.CreateExecutionStrategy().Execute(() => _context.SaveChanges());
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_transaction is not null || _context.Database.CurrentTransaction is not null)
        {
            return _context.SaveChangesAsync(cancellationToken);
        }

        return _context.Database.CreateExecutionStrategy()
            .ExecuteAsync(() => _context.SaveChangesAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active on this unit of work.");
        }

        if (_context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The DbContext already has a transaction that is not owned by this unit of work.");
        }

        if (_context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            throw new InvalidOperationException(
                "The configured provider uses a retrying execution strategy, which does not "
                + "support user-initiated transactions. Use ExecuteInTransactionAsync so the "
                + "transaction and all work are retried as one unit.");
        }

        _transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IDbContextTransaction transaction = _transaction
            ?? throw new InvalidOperationException("No transaction is active on this unit of work.");

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transaction = null;
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IDbContextTransaction transaction = _transaction
            ?? throw new InvalidOperationException("No transaction is active on this unit of work.");

        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transaction = null;
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteInTransactionAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();

        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active on this unit of work.");
        }

        if (_context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The DbContext already has a transaction that is not owned by this unit of work.");
        }

        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(
            async () =>
            {
                IDbContextTransaction transaction = await _context.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                _transaction = transaction;
                try
                {
                    TResult result = await operation(cancellationToken).ConfigureAwait(false);
                    await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original failure. The caller must inspect the transaction state.
                    }

                    throw;
                }
                finally
                {
                    _transaction = null;
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IDbContextTransaction? transaction = _transaction;
        _transaction = null;

        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        catch
        {
            // Disposal must not hide the original failure.
        }
        finally
        {
            transaction.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IDbContextTransaction? transaction = _transaction;
        _transaction = null;

        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Disposal must not hide the original failure.
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
