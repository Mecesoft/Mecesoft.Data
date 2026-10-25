using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Data.EntityFrameworkCore.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Mecesoft.Data.EntityFrameworkCore.Tests;

public sealed class Order : ITenantEntity<Guid>, ISoftDeletable
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public long Version { get; set; } = 1;

    public List<OrderLine> Lines { get; } = [];

    public List<OrderNote> Notes { get; } = [];
}

public sealed class OrderLine : ITenantEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid TenantId { get; set; }

    public Order? Order { get; set; }
}

public sealed class OrderNote
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid TenantId { get; set; }

    public string Text { get; set; } = string.Empty;

    public Order? Order { get; set; }
}

public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class StringTenantEntity : ITenantEntity<string>
{
    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class StringTenantDbContext : MecesoftDbContext
{
    public StringTenantDbContext(
        DbContextOptions<StringTenantDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

    protected override ITenantCollationVerifier? TenantCollationVerifier
        => SqliteStringTenantCollationVerifier.Instance;

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class UnverifiedStringDbContext : MecesoftDbContext
{
    public UnverifiedStringDbContext(
        DbContextOptions<UnverifiedStringDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<StringTenantEntity> Items => Set<StringTenantEntity>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StringTenantEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.TenantId).IsRequired();
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class TestDbContext : MecesoftDbContext
{
    public TestDbContext(
        DbContextOptions<TestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<OrderNote>();

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            entity.Ignore(order => order.Notes);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.Property(line => line.TenantId).IsRequired();
            entity.HasOne(line => line.Order)
                .WithMany(order => order.Lines)
                .HasForeignKey(line => line.OrderId);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Message).IsRequired();
        });
    }
}

public sealed class SecondTestDbContext : MecesoftDbContext
{
    public SecondTestDbContext(
        DbContextOptions<SecondTestDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore<OrderNote>();

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
            entity.Property(item => item.Version)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            entity.Ignore(item => item.Notes);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.Property(line => line.TenantId).IsRequired();
            entity.HasOne(line => line.Order)
                .WithMany(order => order.Lines)
                .HasForeignKey(line => line.OrderId);
        });
    }
}

public sealed class UnmappedDbContext : MecesoftDbContext
{
    public UnmappedDbContext(
        DbContextOptions<UnmappedDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

public sealed class DuplicateTenantDbContext : MecesoftDbContext
{
    public DuplicateTenantDbContext(
        DbContextOptions<DuplicateTenantDbContext> options,
        ITenantScopeStore tenantScope,
        IEnumerable<ITenantEntityMapping> tenantMappings,
        IEnumerable<ISoftDeleteEntityMapping> softDeleteMappings)
        : base(options, tenantScope, tenantMappings, softDeleteMappings)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).IsRequired();
        });
    }
}

internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteTestDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public SqliteConnection Connection => _connection;

    public static async Task<SqliteTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        return new SqliteTestDatabase(connection);
    }

    public DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        return new DbContextOptionsBuilder<TContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

internal static class TestContexts
{
    public static TestDbContext Create(
        SqliteTestDatabase database,
        ITenantScopeStore store,
        params ITenantEntityMapping[] mappings)
    {
        return new TestDbContext(
            database.Options<TestDbContext>(),
            store,
            mappings.Append(OrderLineTenantMapping()).ToArray(),
            []);
    }

    public static TestDbContext CreateWithSoftDelete(
        SqliteTestDatabase database,
        ITenantScopeStore store,
        ISoftDeleteEntityMapping softDeleteMapping,
        params ITenantEntityMapping[] mappings)
    {
        return new TestDbContext(
            database.Options<TestDbContext>(),
            store,
            mappings.Append(OrderLineTenantMapping()).ToArray(),
            [softDeleteMapping]);
    }

    public static TestDbContext CreateWithInterceptor(
        SqliteTestDatabase database,
        ITenantScopeStore store,
        CapturingCommandInterceptor interceptor,
        params ITenantEntityMapping[] mappings)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(database.Connection)
            .AddInterceptors(interceptor)
            .Options;

        return new TestDbContext(
            options,
            store,
            mappings.Append(OrderLineTenantMapping()).ToArray(),
            []);
    }

    public static TestDbContext CreateWithTransactionInterceptor(
        SqliteTestDatabase database,
        ITenantScopeStore store,
        DbTransactionInterceptor interceptor,
        params ITenantEntityMapping[] mappings)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(database.Connection)
            .AddInterceptors(interceptor)
            .Options;

        return new TestDbContext(
            options,
            store,
            mappings.Append(OrderLineTenantMapping()).ToArray(),
            []);
    }

    public static TestDbContext CreateWithSaveChangesInterceptor(
        SqliteTestDatabase database,
        ITenantScopeStore store,
        SaveChangesInterceptor interceptor,
        params ITenantEntityMapping[] mappings)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(database.Connection)
            .AddInterceptors(interceptor)
            .Options;

        return new TestDbContext(
            options,
            store,
            mappings.Append(OrderLineTenantMapping()).ToArray(),
            []);
    }

    public static TenantEntityMapping<Order, Guid> OrderTenantMapping()
    {
        return new TenantEntityMapping<Order, Guid>(order => order.TenantId);
    }

    public static TenantEntityMapping<OrderLine, Guid> OrderLineTenantMapping()
    {
        return new TenantEntityMapping<OrderLine, Guid>(line => line.TenantId);
    }
}

internal static class TestSeeds
{
    public static async Task SaveOrdersAsync(
        TestDbContext context,
        TenantScopeStore store,
        Guid tenantId,
        params Order[] orders)
    {
        store.Clear();
        if (!store.TrySet(tenantId, TenantOperation.Create))
        {
            throw new InvalidOperationException("The test tenant could not be set.");
        }

        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);

        Order[] softDeleted = orders.Where(order => order.IsDeleted).ToArray();
        foreach (Order order in softDeleted)
        {
            order.IsDeleted = false;
            order.DeletedAtUtc = null;
        }

        context.Orders.AddRange(orders);
        await context.SaveChangesAsync();

        foreach (Order order in softDeleted)
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE Orders SET IsDeleted = 1, DeletedAtUtc = NULL WHERE Id = {0}",
                order.Id);
            order.IsDeleted = true;
        }

        context.ChangeTracker.Clear();
    }

    public static async Task SaveStringItemsAsync(
        StringTenantDbContext context,
        TenantScopeStore store,
        string tenantId,
        params StringTenantEntity[] items)
    {
        store.Clear();
        if (!store.TrySet(tenantId, TenantOperation.Create))
        {
            throw new InvalidOperationException("The test tenant could not be set.");
        }

        store.AuthorizeWriteForTesting(tenantId, TenantOperation.Create);

        context.Items.AddRange(items);
        await context.SaveChangesAsync();
    }
}
