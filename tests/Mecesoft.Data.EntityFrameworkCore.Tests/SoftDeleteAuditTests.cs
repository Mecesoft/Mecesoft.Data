using Mecesoft.Data.Abstractions.Auditing;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore;
using Mecesoft.Data.EntityFrameworkCore.Auditing;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Microsoft.EntityFrameworkCore;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class SoftDeleteAuditTests
{
    [Fact]
    public async Task SoftDeleteServiceSoftDeletesAndRestores()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        await using var context = new LegacyDbContext(database.Options<LegacyDbContext>());
        await context.Database.EnsureCreatedAsync();

        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var entity = new LegacySoftEntity { Id = Guid.NewGuid(), Name = "service" };
        context.Add(entity);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new SoftDeleteService<LegacyDbContext, LegacySoftEntity, Guid>(
            context,
            item => item.Id,
            clock);

        Assert.True(await service.SoftDeleteAsync(entity.Id));
        context.ChangeTracker.Clear();
        Assert.Empty(await context.SoftEntities.ToListAsync());

        LegacySoftEntity deleted = await context.SoftEntities
            .IgnoreQueryFilters([MecesoftQueryFilterKeys.SoftDelete])
            .SingleAsync(item => item.Id == entity.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(clock.GetUtcNow(), deleted.DeletedAtUtc);

        Assert.True(await service.SoftDeleteAsync(entity.Id));
        Assert.True(await service.RestoreAsync(entity.Id));
        context.ChangeTracker.Clear();

        LegacySoftEntity restored = await context.SoftEntities.SingleAsync(item => item.Id == entity.Id);
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DeletedAtUtc);

        Assert.True(await service.RestoreAsync(entity.Id));
        Assert.False(await service.SoftDeleteAsync(Guid.NewGuid()));
        Assert.False(await service.RestoreAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SoftDeleteServiceRefusesMecesoftDbContext()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var store = new Mecesoft.Data.EntityFrameworkCore.Tenancy.TenantScopeStore();
        await using TestDbContext context = TestContexts.Create(
            database,
            store,
            TestContexts.OrderTenantMapping());
        await context.Database.EnsureCreatedAsync();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new SoftDeleteService<TestDbContext, Order, Guid>(context, order => order.Id));

        Assert.Contains("MecesoftDbContext", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoftDeleteServiceRequiresNamedFilter()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<LegacyUnfilteredDbContext>()
            .UseSqlite(database.Connection)
            .Options;
        await using var context = new LegacyUnfilteredDbContext(options);
        await context.Database.EnsureCreatedAsync();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new SoftDeleteService<LegacyUnfilteredDbContext, LegacySoftEntity, Guid>(
                context,
                item => item.Id));

        Assert.Contains("ApplyMecesoftSoftDeleteFilters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditInterceptorSetsCreationAndModificationValues()
    {
        await using SqliteTestDatabase database = await SqliteTestDatabase.CreateAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<LegacyDbContext>()
            .UseSqlite(database.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(
                clock,
                new FixedAuditUserProvider("tester")))
            .Options;

        await using var context = new LegacyDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var entity = new LegacyAuditedEntity { Id = Guid.NewGuid(), Name = "audited" };
        context.Add(entity);
        await context.SaveChangesAsync();

        Assert.Equal(clock.GetUtcNow(), entity.CreatedAtUtc);
        Assert.Equal("tester", entity.CreatedBy);
        Assert.Equal(clock.GetUtcNow(), entity.UpdatedAtUtc);
        Assert.Equal("tester", entity.UpdatedBy);

        DateTimeOffset firstUpdate = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMinutes(5));
        entity.Name = "updated";
        await context.SaveChangesAsync();

        Assert.Equal(firstUpdate, entity.CreatedAtUtc);
        Assert.Equal(clock.GetUtcNow(), entity.UpdatedAtUtc);
        Assert.Equal("tester", entity.UpdatedBy);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class FixedAuditUserProvider(string userId) : IAuditUserProvider
    {
        public string? UserId { get; } = userId;
    }
}
