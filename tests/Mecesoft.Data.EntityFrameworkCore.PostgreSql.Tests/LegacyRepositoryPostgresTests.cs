using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tests;

public sealed class LegacyRepositoryPostgresTests
{
    [PostgresFact]
    public async Task LegacyRepositoryCrudAndUnitOfWorkWorkOnPostgreSql()
    {
        PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync();
        try
        {
            await using var context = new PostgresLegacyDbContext(database.Options<PostgresLegacyDbContext>());
            await context.Database.EnsureCreatedAsync();

            var repository = new Repository<PostgresLegacyProduct, PostgresLegacyDbContext>(context);
            await using var unitOfWork = new UnitOfWork<PostgresLegacyDbContext>(context);

            var product = new PostgresLegacyProduct
            {
                Id = Guid.NewGuid(),
                Name = "postgres",
                Price = 12m,
            };
            await repository.AddAsync(product);
            await unitOfWork.SaveChangesAsync();

            Assert.Equal("postgres", await repository.GetByExpressionDtoAsync(
                item => item.Id == product.Id,
                item => item.Name));

            context.ChangeTracker.Clear();
            PostgresLegacyProduct detached = await repository.GetAll()
                .SingleAsync(item => item.Id == product.Id);
            detached.Name = "postgres updated";
            detached.Version = 2;
            repository.Update(detached, originalConcurrencyToken: 1L);
            await unitOfWork.SaveChangesAsync();
            context.ChangeTracker.Clear();

            PostgresLegacyProduct updated = await repository.GetAll()
                .SingleAsync(item => item.Id == product.Id);
            Assert.Equal("postgres updated", updated.Name);
            Assert.Equal(2, updated.Version);

            updated.Name = "stale";
            repository.Update(updated, originalConcurrencyToken: 1L);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => unitOfWork.SaveChangesAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}

public sealed class PostgresLegacyProduct
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public long Version { get; set; } = 1;
}

public sealed class PostgresLegacyDbContext : DbContext
{
    public PostgresLegacyDbContext(DbContextOptions<PostgresLegacyDbContext> options)
        : base(options)
    {
    }

    public DbSet<PostgresLegacyProduct> Products => Set<PostgresLegacyProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PostgresLegacyProduct>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        });
    }
}
