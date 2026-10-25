using Mecesoft.Data.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mecesoft.Data.EntityFrameworkCore.Auditing;

/// <summary>
/// Optionally fills creation and modification audit fields before EF Core saves changes.
/// </summary>
/// <remarks>
/// <para>
/// The interceptor only touches entities that implement <see cref="ICreationAuditable"/> or
/// <see cref="IModificationAuditable"/>. It never adds audit fields to a model and never requires
/// auditing. It also does not implement soft delete or tenant isolation.
/// </para>
/// <para>
/// Audit values are set from <see cref="TimeProvider"/> and <see cref="IAuditUserProvider"/>.
/// Application code remains responsible for authorization and for ensuring that audit values are
/// not supplied by an untrusted client.
/// </para>
/// </remarks>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly TimeProvider _timeProvider;
    private readonly IAuditUserProvider? _userProvider;

    /// <summary>
    /// Initializes a new audit interceptor.
    /// </summary>
    /// <param name="timeProvider">The clock used for audit timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="userProvider">The optional current-user provider.</param>
    public AuditSaveChangesInterceptor(
        TimeProvider? timeProvider = null,
        IAuditUserProvider? userProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _userProvider = userProvider;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyAuditValues(eventData?.Context);
        return base.SavingChanges(eventData!, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditValues(eventData?.Context);
        return base.SavingChangesAsync(eventData!, result, cancellationToken);
    }

    private void ApplyAuditValues(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string? userId = _userProvider?.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.State == EntityState.Added
                && entry.Entity is ICreationAuditable creation)
            {
                creation.CreatedAtUtc = now;
                creation.CreatedBy = userId;
            }

            if (entry.Entity is IModificationAuditable modification)
            {
                modification.UpdatedAtUtc = now;
                modification.UpdatedBy = userId;
            }
        }
    }
}
