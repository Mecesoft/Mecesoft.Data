using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class UnitOfWorkCompatibilityTests
{
    [Fact]
    public async Task UnitOfWorkSavesAcrossMultipleRepositories()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var products = new Repository<LegacyProduct, LegacyDbContext>(context);
        var tags = new Repository<LegacyProductTag, LegacyDbContext>(context);
        await using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        var product = new LegacyProduct { Id = Guid.NewGuid(), Name = "Shared", Price = 1m };
        await products.AddAsync(product);
        await tags.AddAsync(new LegacyProductTag
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            Label = "tag",
        });

        Assert.Equal(0, await context.Products.CountAsync());
        Assert.Equal(2, await unitOfWork.SaveChangesAsync());
        Assert.Equal(1, await context.Products.CountAsync());
        Assert.Equal(1, await context.ProductTags.CountAsync());
    }

    [Fact]
    public async Task UnitOfWorkSavesChangesSynchronously()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);
        repository.Add(new LegacyProduct { Id = Guid.NewGuid(), Name = "sync", Price = 6m });

        Assert.Equal(1, unitOfWork.SaveChanges());
        context.ChangeTracker.Clear();
        Assert.Equal(1, await repository.GetAll().CountAsync());
    }

    [Fact]
    public async Task UnitOfWorkCommitAndRollbackControlDurability()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        await using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await unitOfWork.BeginTransactionAsync();
        Assert.True(unitOfWork.HasActiveTransaction);
        Assert.NotNull(unitOfWork.CurrentTransaction);
        await repository.AddAsync(new LegacyProduct { Id = Guid.NewGuid(), Name = "committed", Price = 1m });
        await unitOfWork.SaveChangesAsync();
        await unitOfWork.CommitTransactionAsync();
        Assert.False(unitOfWork.HasActiveTransaction);

        await unitOfWork.BeginTransactionAsync();
        await repository.AddAsync(new LegacyProduct { Id = Guid.NewGuid(), Name = "rolled back", Price = 2m });
        await unitOfWork.SaveChangesAsync();
        await unitOfWork.RollbackTransactionAsync();

        context.ChangeTracker.Clear();
        Assert.Equal(1, await repository.GetAll().CountAsync());
        Assert.Equal("committed", await repository.GetByExpressionDtoAsync(
            item => item.Name == "committed",
            item => item.Name));
    }

    [Fact]
    public async Task UnitOfWorkExecuteInTransactionCommitsWork()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        await using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await repository.AddAsync(
                new LegacyProduct { Id = Guid.NewGuid(), Name = "inside", Price = 3m },
                token);
        });

        context.ChangeTracker.Clear();
        Assert.Equal(1, await repository.GetAll().CountAsync());
    }

    [Fact]
    public async Task UnitOfWorkExecuteInTransactionRollsBackOnFailure()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        await using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                await repository.AddAsync(
                    new LegacyProduct { Id = Guid.NewGuid(), Name = "failure", Price = 4m },
                    token);
                throw new InvalidOperationException("stop");
            }));

        context.ChangeTracker.Clear();
        Assert.Equal(0, await repository.GetAll().CountAsync());
        Assert.False(unitOfWork.HasActiveTransaction);
    }

    [Fact]
    public async Task UnitOfWorkRejectsNestedAndMissingTransactions()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();
        await using var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.CommitTransactionAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.RollbackTransactionAsync());

        await unitOfWork.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.BeginTransactionAsync());
        await unitOfWork.RollbackTransactionAsync();
    }

    [Fact]
    public async Task UnitOfWorkDisposeRollsBackAnActiveTransaction()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);

        await using (var unitOfWork = new UnitOfWork<LegacyDbContext>(context))
        {
            await unitOfWork.BeginTransactionAsync();
            await repository.AddAsync(new LegacyProduct
            {
                Id = Guid.NewGuid(),
                Name = "disposed",
                Price = 5m,
            });
            await unitOfWork.SaveChangesAsync();
        }

        context.ChangeTracker.Clear();
        Assert.Equal(0, await repository.GetAll().CountAsync());
    }
}
