using System.ComponentModel.DataAnnotations;
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Mecesoft.Data.EntityFrameworkCore.SqlServer.Tests;

public sealed class LegacyRepositorySqlServerTests
{
    [SqlServerFact]
    public async Task LegacyRepositoryRowVersionConcurrencyIsEnforcedForDetachedUpdates()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await using var context = new SqlLegacyDbContext(database.Options<SqlLegacyDbContext>());
            await context.Database.EnsureCreatedAsync();

            var repository = new Repository<SqlLegacyRowVersionEntity, SqlLegacyDbContext>(context);
            var unitOfWork = new UnitOfWork<SqlLegacyDbContext>(context);
            var entity = new SqlLegacyRowVersionEntity
            {
                Id = Guid.NewGuid(),
                Name = "original",
            };

            await repository.AddAsync(entity);
            await unitOfWork.SaveChangesAsync();
            Assert.NotEmpty(entity.RowVersion);

            byte[] original = entity.RowVersion.ToArray();
            context.ChangeTracker.Clear();

            SqlLegacyRowVersionEntity detached = await repository.GetAll()
                .SingleAsync(item => item.Id == entity.Id);
            detached.Name = "updated";
            repository.Update(detached, original);
            await unitOfWork.SaveChangesAsync();
            context.ChangeTracker.Clear();

            SqlLegacyRowVersionEntity updated = await repository.GetAll()
                .SingleAsync(item => item.Id == entity.Id);
            Assert.Equal("updated", updated.Name);
            Assert.False(original.AsSpan().SequenceEqual(updated.RowVersion));

            updated.Name = "stale";
            repository.Update(updated, original);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => unitOfWork.SaveChangesAsync());
            context.ChangeTracker.Clear();

            SqlLegacyRowVersionEntity persisted = await repository.GetAll()
                .SingleAsync(item => item.Id == entity.Id);
            Assert.Equal("updated", persisted.Name);

            var metadata = context.Model.FindEntityType(typeof(SqlLegacyRowVersionEntity));
            var rowVersion = metadata?.FindProperty(nameof(SqlLegacyRowVersionEntity.RowVersion));
            Assert.NotNull(rowVersion);
            Assert.True(rowVersion!.IsConcurrencyToken);
            Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
        }
        finally
        {
            await database.DisposeAsync<SqlLegacyDbContext>(
                options => new SqlLegacyDbContext(options));
        }
    }

    [SqlServerFact]
    public async Task LegacyUnitOfWorkWorksWithRetryingExecutionStrategy()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await using var context = new SqlLegacyDbContext(
                database.OptionsWithRetryingExecutionStrategy<SqlLegacyDbContext>());
            await context.Database.EnsureCreatedAsync();
            var repository = new Repository<SqlLegacyRowVersionEntity, SqlLegacyDbContext>(context);
            await using var unitOfWork = new UnitOfWork<SqlLegacyDbContext>(context);

            await unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await repository.AddAsync(
                    new SqlLegacyRowVersionEntity
                    {
                        Id = Guid.NewGuid(),
                        Name = "retry strategy",
                    },
                    token);
            });

            context.ChangeTracker.Clear();
            Assert.Single(await repository.GetAll().ToListAsync());

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => unitOfWork.BeginTransactionAsync());
            Assert.Contains("ExecuteInTransactionAsync", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await database.DisposeAsync<SqlLegacyDbContext>(
                options => new SqlLegacyDbContext(options));
        }
    }

    [SqlServerFact]
    public async Task LegacyRepositoryUnitOfWorkRollbackDoesNotPersist()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await using var context = new SqlLegacyDbContext(database.Options<SqlLegacyDbContext>());
            await context.Database.EnsureCreatedAsync();
            var repository = new Repository<SqlLegacyRowVersionEntity, SqlLegacyDbContext>(context);
            await using var unitOfWork = new UnitOfWork<SqlLegacyDbContext>(context);

            await unitOfWork.BeginTransactionAsync();
            await repository.AddAsync(new SqlLegacyRowVersionEntity
            {
                Id = Guid.NewGuid(),
                Name = "rolled back",
            });
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.RollbackTransactionAsync();

            context.ChangeTracker.Clear();
            Assert.Empty(await repository.GetAll().ToListAsync());
        }
        finally
        {
            await database.DisposeAsync<SqlLegacyDbContext>(
                options => new SqlLegacyDbContext(options));
        }
    }
}

public sealed class SqlLegacyRowVersionEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SqlLegacyDbContext : DbContext
{
    public SqlLegacyDbContext(DbContextOptions<SqlLegacyDbContext> options)
        : base(options)
    {
    }

    public DbSet<SqlLegacyRowVersionEntity> Entities => Set<SqlLegacyRowVersionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<SqlLegacyRowVersionEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.RowVersion).IsRowVersion();
        });
    }
}
