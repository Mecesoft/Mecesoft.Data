# Changelog

All notable changes to Mecesoft.Data are documented here.

## [1.1.0] - 2026-10-25

### Added

- `Mecesoft.Repository.IRepository<TEntity>` and `Mecesoft.Repository.Repository<TEntity,TContext>`
  compatibility layer with the Mecesoft.Repository v1 method signatures, including
  `GetAll`, `Where`, `GetByExpressionAsync`, `GetByExpressionDtoAsync`, `GetFirstAsync`, `Any`,
  `Add`, `AddAsync`, `AddRangeAsync`, `Update`, `UpdateRange`, `Delete`, `DeleteRange`,
  `DeleteByIdAsync`, and `DeleteByExpressionAsync`.
- `IQueryable<TEntity>` composition with `Include`, `ThenInclude`, `AsNoTracking`, `Select`,
  `OrderBy`, and projection support.
- Pagination helpers `ToPageAsync` and `ToStablePageAsync`.
- General-purpose `Mecesoft.Repository.IUnitOfWork` and `UnitOfWork<TContext>` with
  `SaveChanges`, `SaveChangesAsync`, explicit transaction methods, disposal rollback, and
  retry-safe `ExecuteInTransactionAsync` overloads.
- Detached optimistic-concurrency update helper
  `Repository.Update(entity, originalConcurrencyToken)`.
- Standard `DbContext`, `IdentityDbContext`, and custom context support without requiring
  `MecesoftDbContext`.
- Optional `SoftDeleteService<TContext,TEntity,TKey>` and
  `ModelBuilder.ApplyMecesoftSoftDeleteFilters()` with the named `Mecesoft.SoftDelete` filter.
- Optional `ICreationAuditable`, `IModificationAuditable`, `IAuditable`,
  `IAuditUserProvider`, and `AuditSaveChangesInterceptor`.
- Database-per-tenant DI setup through `ITenantConnectionStringResolver` and
  `AddMecesoftTenantData<TContext,TResolver>`.
- DI helpers `AddMecesoftData`, `AddMecesoftRepository`, `AddMecesoftUnitOfWork`, and
  `AddMecesoftAuditing`.
- Migration guide and expanded README examples.

### Changed

- Package version is `1.1.0`. All 1.0.0 public APIs remain available.
- `Mecesoft.Data.EntityFrameworkCore` remains provider-neutral; SQL Server and PostgreSQL
  provider packages stay optional.

### Security

- The compatibility repository does not weaken tenant filters, read grants, write grants, or
  `SaveChanges` validation. On `MecesoftDbContext`, all mandatory controls remain active.
- `includeDeleted` removes only the named soft-delete filter. It is rejected for non-soft-deletable
  entities, for `MecesoftDbContext`, and when the named filter is not configured. The legacy
  behavior of removing every query filter is intentionally not reproduced.
- `SoftDeleteService` refuses to run against `MecesoftDbContext`; the authorized soft-delete
  repository must be used there.
- Database-per-tenant resolvers are registered as scoped services and must fail closed when no
  tenant is resolved.

### Compatibility notes

- `DeleteByExpressionAsync` and `DeleteByIdAsync` are no-ops when no row matches. The v1
  implementation threw indirectly by removing a `null` entity.
- Physical deletes remain physical; use `SoftDeleteService` or the secure soft-delete repository
  for soft-delete semantics.
- `Update(entity, originalConcurrencyToken)` pins the original token for detached updates. For
  application-managed tokens, set the new current value on the entity before calling it.

## [1.0.0] - 2026-10-25

### Added

- Provider-independent data access abstractions.
- EF Core relational integration with secure tenant and soft-delete named filters.
- Fail-closed tenant ownership, read/write grants, global write authorization, and cascade rules.
- Secure read, write, and soft-delete repositories with unit-of-work transaction ownership.
- SQL Server and PostgreSQL tenant collation and relationship verifiers.
- SourceLink-enabled NuGet packages and symbol packages.
