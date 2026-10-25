# Mecesoft.Repository v1 → Mecesoft.Data 1.1 Migration Guide

Mecesoft.Data 1.1 keeps the Mecesoft.Repository v1 public repository surface available in the
`Mecesoft.Repository` namespace. Most applications can migrate by changing the package reference
and adjusting the security-sensitive cases described below.

## 1. Replace the package

```bash
dotnet remove package Mecesoft.Repository
dotnet add package Mecesoft.Data.EntityFrameworkCore --version 1.1.0
```

`Mecesoft.Data.EntityFrameworkCore` references `Mecesoft.Data.Abstractions`, so a separate
abstractions reference is optional. The SQL Server and PostgreSQL tenant packages remain optional.

The old `Mecesoft.Repository` namespace is preserved, so these usings continue to compile:

```csharp
using Mecesoft.Repository;
```

## 2. Register a standard DbContext and the compatibility services

```csharp
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

services.AddMecesoftData<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

services.AddMecesoftRepository<AppDbContext, Order>();
services.AddMecesoftRepository<AppDbContext, OrderLine>();
```

Resolve `IRepository<Order>` and `IUnitOfWork` from a scoped provider.

If application code imports both `Mecesoft.Repository` and
`Mecesoft.Data.Abstractions.Transactions`, alias the compatibility `IUnitOfWork` or the secure
`IUnitOfWork` to avoid an ambiguous type name.

If you already register the context manually, keep doing so and add:

```csharp
services.AddMecesoftRepository<AppDbContext, Order>();
services.AddMecesoftUnitOfWork<AppDbContext>();
```

`AddMecesoftData` is only for standard, Identity, or custom `DbContext` types. A
`MecesoftDbContext` must be registered with `AddMecesoftDbContext` and used with the secure
repositories.

## 3. Call sites

| v1 API | Mecesoft.Data 1.1 |
|---|---|
| `new Repository<Order, AppDbContext>(context)` | Same class and constructor |
| `GetAll`, `Where` | Same, including `IQueryable` composition |
| `GetByExpressionAsync`, `GetByExpressionDtoAsync`, `GetFirstAsync` | Same signatures |
| `Any`, `AnyAsync` | Same |
| `Add`, `AddAsync`, `AddRangeAsync` | Same |
| `Update`, `UpdateRange` | Same |
| `Delete`, `DeleteRange`, `DeleteByIdAsync`, `DeleteByExpressionAsync` | Same signatures |
| `GetByExpressionWithTracking` / async | Same |
| `GetAllExpressionWithTrackingAsync` | Same |
| `SaveChanges` on the context | `IUnitOfWork.SaveChanges` / `SaveChangesAsync` (or the context) |
| Pagination | `ToPageAsync` / `ToStablePageAsync` extensions |

## 4. Behavioral differences

### `includeDeleted`

v1 called `IgnoreQueryFilters()`, which removed every filter. That could silently disable a
tenant or application filter. Mecesoft.Data 1.1 removes only the named
`Mecesoft.SoftDelete` filter.

Requirements:

- The entity implements `ISoftDeletable`.
- The context is not a `MecesoftDbContext`.
- The model has the named filter:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyMecesoftSoftDeleteFilters();
}
```

Otherwise `includeDeleted: true` throws an `InvalidOperationException` instead of bypassing an
unrelated filter.

### Delete behavior

`DeleteByIdAsync` and `DeleteByExpressionAsync` are no-ops when no row matches. The v1
implementation passed a `null` entity to `DbSet.Remove`, which failed. Physical deletes are still
physical; use soft delete when the entity implements `ISoftDeletable`.

### Detached concurrency

For a detached update with an optimistic token:

```csharp
byte[] original = detached.RowVersion.ToArray();
detached.Name = "updated";
repository.Update(detached, original);
await unitOfWork.SaveChangesAsync();
```

The original value is used in the `WHERE` clause. A mismatch throws
`DbUpdateConcurrencyException`.

### Unit of work

v1 callers usually called `context.SaveChanges` directly. That still works for a standard
`DbContext`. For an explicit transaction boundary, use `UnitOfWork<TContext>`:

```csharp
IUnitOfWork unitOfWork = new UnitOfWork<AppDbContext>(context);

await repository.AddAsync(order);
await unitOfWork.SaveChangesAsync();

await unitOfWork.ExecuteInTransactionAsync(async token =>
{
    await repository.AddAsync(order, token);
    await repository.AddAsync(audit, token);
});
```

`ExecuteInTransactionAsync` is the only transaction path that is safe with a retrying EF Core
execution strategy. `BeginTransactionAsync` fails fast on such a provider and explains the
alternative.

## 5. Tenant scenarios

### No tenant

Use the compatibility repository and `UnitOfWork<TContext>` exactly as in v1.

### Database per tenant

Register a scoped resolver and let `AddMecesoftTenantData` build the context:

```csharp
public interface ITenantContext
{
    string? ConnectionString { get; }
}

public sealed class TenantConnectionStringResolver(
    ITenantContext tenantContext) : ITenantConnectionStringResolver
{
    private readonly ITenantContext _tenantContext = tenantContext;

    public string ResolveConnectionString()
    {
        string? connectionString = _tenantContext.ConnectionString;
        return connectionString
            ?? throw new InvalidOperationException("No tenant is resolved.");
    }
}

services.AddMecesoftTenantData<AppDbContext, TenantConnectionStringResolver>(
    (options, connectionString) => options.UseSqlServer(connectionString));
```

The resolver must be scoped and must never fall back to a shared database when no tenant is
resolved.

### Row per tenant

Use `MecesoftDbContext` with the secure `EfReadRepository`, `EfWriteRepository`,
`EfSoftDeleteRepository`, and `EfUnitOfWork` APIs. The compatibility repository remains usable for
compile compatibility, but:

- Tenant query filters remain active.
- Direct reads without a resolver-issued read grant fail closed.
- Writes still require write grants, a unit-of-work transaction, and `SaveChanges` validation.
- `includeDeleted` is rejected on `MecesoftDbContext`; use the authorized soft-delete repository.

Do not replace the secure repositories with the compatibility layer when row-level isolation is
required.

## 6. Security checklist

- Do not call `IgnoreQueryFilters()` or remove `MecesoftQueryFilterKeys.Tenant`.
- Keep `includeDeleted` limited to the named soft-delete filter.
- Authorize soft-delete, restore, and audit operations in application code when using the
  non-tenant services.
- Keep the database-per-tenant resolver scoped and fail-closed.
- Continue using the secure repositories for row-per-tenant data.
- Run `ValidateTenantSecurity()` at startup for string-tenant `MecesoftDbContext` configurations.

## 7. Verification

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build
```

Run the SQL Server and PostgreSQL test projects with the documented environment variables when
the application uses those providers.
