using System.Linq.Expressions;
using System.Reflection;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class LegacyRepositoryCompatibilityTests
{
    [Fact]
    public void LegacyInterfaceExposesTheV1Surface()
    {
        Type contract = typeof(IRepository<LegacyProduct>);
        MethodInfo[] methods = contract.GetMethods();

        Assert.Contains(methods, method =>
            method.Name == nameof(IRepository<LegacyProduct>.GetAll)
            && method.ReturnType == typeof(IQueryable<LegacyProduct>)
            && method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(bool), typeof(bool)]));
        Assert.Contains(methods, method =>
            method.Name == nameof(IRepository<LegacyProduct>.Where)
            && method.GetParameters()[0].ParameterType
                == typeof(Expression<Func<LegacyProduct, bool>>));
        Assert.Contains(methods, method =>
            method.Name == nameof(IRepository<LegacyProduct>.GetByExpressionAsync)
            && method.GetParameters()[1].ParameterType == typeof(CancellationToken));
        Assert.Contains(methods, method =>
            method.Name == nameof(IRepository<LegacyProduct>.GetByExpressionDtoAsync));
        Assert.Contains(methods, method =>
            method.Name == nameof(IRepository<LegacyProduct>.DeleteByIdAsync)
            && method.GetParameters()[0].ParameterType == typeof(string));
        Assert.Contains(methods, method => method.Name == nameof(IRepository<LegacyProduct>.AddRangeAsync));
        Assert.Contains(methods, method => method.Name == nameof(IRepository<LegacyProduct>.UpdateRange));
        Assert.Contains(methods, method => method.Name == nameof(IRepository<LegacyProduct>.DeleteRange));
    }

    [Fact]
    public async Task LegacyRepositoryQueriesAndProjections()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await repository.AddRangeAsync(
        [
            new LegacyProduct { Id = Guid.NewGuid(), Name = "Alpha", Price = 10m },
            new LegacyProduct { Id = Guid.NewGuid(), Name = "Beta", Price = 20m },
        ]);
        await unitOfWork.SaveChangesAsync();

        Assert.Equal(2, await repository.GetAll().CountAsync());
        Assert.Equal(1, await repository.Where(product => product.Price > 15m).CountAsync());
        Assert.True(await repository.AnyAsync(product => product.Name == "Alpha"));
        Assert.True(repository.Any(product => product.Name == "Beta"));

        LegacyProduct? alpha = await repository.GetByExpressionAsync(product => product.Name == "Alpha");
        Assert.NotNull(alpha);
        Assert.Equal(10m, alpha!.Price);

        string? name = await repository.GetByExpressionDtoAsync(
            product => product.Name == "Beta",
            product => product.Name);
        Assert.Equal("Beta", name);

        LegacyProduct? first = await repository.GetFirstAsync();
        Assert.NotNull(first);
        Assert.NotNull(repository.GetFirst());
        Assert.NotNull(repository.GetByExpression(product => product.Name == "Alpha"));

        Assert.NotNull(await repository.GetByExpressionWithTrackingAsync(product => product.Name == "Alpha"));
        Assert.NotNull(await repository.GetByExpressionWithTrackingAsync(
            product => product.Name == "Alpha",
            CancellationToken.None));
        Assert.Equal(2, (await repository.GetAllExpressionWithTrackingAsync(
            product => product.Price > 0m,
            CancellationToken.None)).Count);
    }

    [Fact]
    public async Task LegacyRepositoryIncludeThenIncludeAndQueryComposition()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyDbContext>(context);
        var product = new LegacyProduct { Id = Guid.NewGuid(), Name = "With tags", Price = 5m };
        var firstTag = new LegacyProductTag { Id = Guid.NewGuid(), Label = "one" };
        firstTag.Details.Add(new LegacyProductTagDetail { Id = Guid.NewGuid(), Note = "detail" });
        product.Tags.Add(firstTag);
        product.Tags.Add(new LegacyProductTag { Id = Guid.NewGuid(), Label = "two" });
        await repository.AddAsync(product);
        await unitOfWork.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var loaded = await repository.GetAll(asNoTracking: false)
            .Include(item => item.Tags)
            .ThenInclude(tag => tag.Details)
            .SingleAsync(item => item.Id == product.Id);

        Assert.Equal(2, loaded.Tags.Count);
        Assert.All(loaded.Tags, tag => Assert.Equal(product.Id, tag.ProductId));
        Assert.Contains(loaded.Tags, tag => tag.Details.Count == 1);
    }

    [Fact]
    public async Task LegacyRepositoryTrackingAndNoTracking()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        await repository.AddAsync(new LegacyProduct { Id = Guid.NewGuid(), Name = "Tracked", Price = 1m });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        _ = await repository.GetAll().ToListAsync();
        Assert.Empty(context.ChangeTracker.Entries());

        _ = await repository.GetAll(asNoTracking: false).ToListAsync();
        Assert.Single(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LegacyRepositoryAddUpdateDeleteAndRanges()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyDbContext>(context);
        var first = new LegacyProduct { Id = Guid.NewGuid(), Name = "First", Price = 1m };
        var second = new LegacyProduct { Id = Guid.NewGuid(), Name = "Second", Price = 2m };

        await repository.AddAsync(first);
        repository.Add(second);
        await unitOfWork.SaveChangesAsync();

        first.Name = "First updated";
        repository.Update(first);
        var range = new List<LegacyProduct> { first, second };
        repository.UpdateRange(range);
        await unitOfWork.SaveChangesAsync();

        LegacyProduct? updated = await repository.GetByExpressionAsync(item => item.Id == first.Id);
        Assert.Equal("First updated", updated?.Name);

        repository.Delete(second);
        await unitOfWork.SaveChangesAsync();
        Assert.False(await repository.AnyAsync(item => item.Id == second.Id));

        var third = new LegacyProduct { Id = Guid.NewGuid(), Name = "Third", Price = 3m };
        var fourth = new LegacyProduct { Id = Guid.NewGuid(), Name = "Fourth", Price = 4m };
        await repository.AddRangeAsync([third, fourth]);
        await unitOfWork.SaveChangesAsync();
        repository.DeleteRange([third, fourth]);
        await unitOfWork.SaveChangesAsync();
        Assert.False(await repository.AnyAsync(item => item.Id == third.Id));
    }

    [Fact]
    public async Task LegacyRepositoryDeleteByIdAndByExpression()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        var stringRepository = new Repository<LegacyStringEntity, LegacyDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyDbContext>(context);

        await repository.AddAsync(new LegacyProduct { Id = Guid.NewGuid(), Name = "Delete me", Price = 1m });
        await stringRepository.AddAsync(new LegacyStringEntity { Id = "key", Name = "delete-by-id" });
        await unitOfWork.SaveChangesAsync();

        await repository.DeleteByExpressionAsync(item => item.Name == "Delete me");
        await repository.DeleteByExpressionAsync(item => item.Name == "missing");
        await stringRepository.DeleteByIdAsync("key");
        await stringRepository.DeleteByIdAsync("missing");
        await unitOfWork.SaveChangesAsync();

        Assert.Empty(await repository.GetAll().ToListAsync());
        Assert.Empty(await stringRepository.GetAll().ToListAsync());
    }

    [Fact]
    public async Task LegacyRepositoryPaginationHelpers()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        for (int index = 0; index < 5; index++)
        {
            await repository.AddAsync(new LegacyProduct
            {
                Id = Guid.NewGuid(),
                Name = $"Product {index}",
                Price = index,
            });
        }

        await context.SaveChangesAsync();

        PageResult<LegacyProduct> page = await repository.GetAll()
            .OrderBy(product => product.Price)
            .ToPageAsync(new PageRequest(2, 2));
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2m, page.Items[0].Price);
        Assert.Equal(3m, page.Items[1].Price);

        PageResult<LegacyProduct> stable = await repository.GetAll()
            .ToStablePageAsync(new PageRequest(1, 3), product => product.Id);
        Assert.Equal(3, stable.Items.Count);
    }

    [Fact]
    public async Task LegacyRepositoryDetachedUpdateUsesOriginalConcurrencyToken()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyDbContext>(context);
        var product = new LegacyProduct { Id = Guid.NewGuid(), Name = "Original", Price = 1m };
        await repository.AddAsync(product);
        await unitOfWork.SaveChangesAsync();
        context.ChangeTracker.Clear();

        LegacyProduct detached = await repository.GetAll()
            .SingleAsync(item => item.Id == product.Id);
        detached.Name = "Updated";
        detached.Version = 2;
        repository.Update(detached, originalConcurrencyToken: 1L);
        await unitOfWork.SaveChangesAsync();
        context.ChangeTracker.Clear();

        LegacyProduct updated = await repository.GetAll()
            .SingleAsync(item => item.Id == product.Id);
        Assert.Equal("Updated", updated.Name);
        Assert.Equal(2, updated.Version);

        updated.Name = "Stale";
        repository.Update(updated, originalConcurrencyToken: 1L);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => unitOfWork.SaveChangesAsync());
        context.ChangeTracker.Clear();

        LegacyProduct? persisted = await repository.GetAll()
            .SingleAsync(item => item.Id == product.Id);
        Assert.Equal("Updated", persisted.Name);
    }

    [Fact]
    public async Task LegacyRepositoryIncludeDeletedUsesOnlyNamedSoftDeleteFilter()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacySoftEntity, LegacyDbContext>(context);
        var active = new LegacySoftEntity { Id = Guid.NewGuid(), Name = "active" };
        var deleted = new LegacySoftEntity
        {
            Id = Guid.NewGuid(),
            Name = "deleted",
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow,
        };
        context.AddRange(active, deleted);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Single(await repository.GetAll().ToListAsync());
        Assert.Equal(2, await repository.GetAll(includeDeleted: true).CountAsync());
        Assert.NotNull(await repository.GetByExpressionAsync(
            item => item.Name == "deleted",
            includeDeleted: true));
        Assert.Null(await repository.GetByExpressionAsync(item => item.Name == "deleted"));
    }

    [Fact]
    public async Task LegacyRepositoryIncludeDeletedRejectsNonSoftDeleteEntity()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        var repository = new Repository<LegacyProduct, LegacyDbContext>(context);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetAll(includeDeleted: true).ToListAsync());

        Assert.Contains("ISoftDeletable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyRepositoryIncludeDeletedRejectsUnfilteredContext()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<LegacyUnfilteredDbContext>()
            .UseSqlite(database.Connection)
            .Options;
        await using var context = new LegacyUnfilteredDbContext(options);
        var repository = new Repository<LegacySoftEntity, LegacyUnfilteredDbContext>(context);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetAll(includeDeleted: true).ToListAsync());

        Assert.Contains("ApplyMecesoftSoftDeleteFilters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityDbContextWorksWithLegacyRepository()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<LegacyIdentityDbContext>()
            .UseSqlite(database.Connection)
            .Options;
        await using var context = new LegacyIdentityDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var repository = new Repository<LegacyProduct, LegacyIdentityDbContext>(context);
        var unitOfWork = new UnitOfWork<LegacyIdentityDbContext>(context);
        var product = new LegacyProduct { Id = Guid.NewGuid(), Name = "Identity", Price = 9m };
        await repository.AddAsync(product);
        await unitOfWork.SaveChangesAsync();

        Assert.Equal("Identity", await repository.GetByExpressionDtoAsync(
            item => item.Id == product.Id,
            item => item.Name));
    }
}
