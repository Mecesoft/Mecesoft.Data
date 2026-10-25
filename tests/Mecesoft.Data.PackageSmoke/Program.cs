using Mecesoft.Data.Abstractions.Results;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore;
using Mecesoft.Data.EntityFrameworkCore.PostgreSql.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SqlServer.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
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

Console.WriteLine(
    $"{MecesoftQueryFilterKeys.Tenant}|{result.IsSuccess}|{entityType?.Name}|"
    + $"{SqlServerTenantCollationVerifier.Instance.GetType().Name}|"
    + $"{PostgreSqlTenantCollationVerifier.Instance.GetType().Name}");
return 0;

public sealed class SmokeEntity : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;
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
