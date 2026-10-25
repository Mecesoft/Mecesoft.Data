using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Mecesoft.Repository;
using Microsoft.EntityFrameworkCore;

var store = new TenantScopeStore();
if (!store.TrySet("tenant-a", TenantOperation.Read))
{
    return 1;
}

DataResult<bool> result = DataResult.Succeeded(true);
var mapping = new TenantEntityMapping<SmokeEntity, Guid>(entity => entity.TenantId);
var options = new DbContextOptionsBuilder<SmokeDbContext>()
    .UseSqlite("Data Source=:memory:")
    .Options;
await using var context = new SmokeDbContext(options, store, [mapping], []);
var entityType = context.Model.FindEntityType(typeof(SmokeEntity));

string databasePath = Path.Combine(
    Path.GetTempPath(),
    $"mecesoft-data-smoke-{Guid.NewGuid():N}.db");

int legacyCount;
int hiddenCount;
try
{
    var legacyOptions = new DbContextOptionsBuilder<LegacySmokeDbContext>()
        .UseSqlite($"Data Source={databasePath}")
        .Options;
    await using var legacyContext = new LegacySmokeDbContext(legacyOptions);
    await legacyContext.Database.EnsureCreatedAsync();

    var legacyRepository = new Repository<LegacySmokeEntity, LegacySmokeDbContext>(legacyContext);
    await using var legacyUnitOfWork = new UnitOfWork<LegacySmokeDbContext>(legacyContext);
    var legacyEntity = new LegacySmokeEntity { Id = Guid.NewGuid(), Name = "legacy" };
    await legacyRepository.AddAsync(legacyEntity);
    await legacyUnitOfWork.SaveChangesAsync();
    legacyCount = await legacyRepository.GetAll().CountAsync();

    var softDeleteService = new SoftDeleteService<LegacySmokeDbContext, LegacySmokeEntity, Guid>(
        legacyContext,
        entity => entity.Id);
    _ = await softDeleteService.SoftDeleteAsync(legacyEntity.Id);
    hiddenCount = await legacyRepository.GetAll().CountAsync();
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (File.Exists(databasePath))
    {
        File.Delete(databasePath);
    }
}

Console.WriteLine(
    $"{MecesoftQueryFilterKeys.Tenant}|{result.IsSuccess}|{entityType?.Name}|"
    + $"{SqlServerTenantCollationVerifier.Instance.GetType().Name}|"
    + $"{PostgreSqlTenantCollationVerifier.Instance.GetType().Name}|"
    + $"legacy={legacyCount}|visible={hiddenCount}");
return 0;

public sealed class SmokeEntity : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class LegacySmokeEntity : ISoftDeletable
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class LegacySmokeDbContext : DbContext
{
    public LegacySmokeDbContext(DbContextOptions<LegacySmokeDbContext> options)
        : base(options)
    {
    }

    public DbSet<LegacySmokeEntity> Entities => Set<LegacySmokeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LegacySmokeEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });
        modelBuilder.ApplyMecesoftSoftDeleteFilters();
    }
}

public sealed class SmokeDbContext : MecesoftDbContext
{
    public SmokeDbContext(
        DbContextOptions<SmokeDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<Mecesoft.Data.EntityFrameworkCore.SoftDelete.ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<SmokeEntity> Entities => Set<SmokeEntity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SmokeEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.Property(item => item.Name).IsRequired();
        });
    }
}
