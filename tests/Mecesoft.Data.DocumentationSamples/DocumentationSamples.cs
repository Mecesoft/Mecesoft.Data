using System.ComponentModel.DataAnnotations;
using Mecesoft.Data.Abstractions.Auditing;
using Mecesoft.Data.Abstractions.Pagination;
using Mecesoft.Data.Abstractions.SoftDelete;
using Mecesoft.Data.Abstractions.Tenancy;
using Mecesoft.Data.EntityFrameworkCore.Auditing;
using Mecesoft.Data.EntityFrameworkCore.SoftDelete;
using Mecesoft.Repository;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mecesoft.Data.DocumentationSamples;

/// <summary>
/// Compile-only examples mirrored from the README and migration guide.
/// </summary>
public static class DocumentationSamples
{
    public static void RegisterStandardContext(IServiceCollection services, string connectionString)
    {
        services.AddMecesoftData<AppDbContext>(options =>
            options.UseSqlServer(connectionString));
        services.AddMecesoftRepository<AppDbContext, Order>();
        services.AddMecesoftRepository<AppDbContext, OrderLine>();
    }

    public static void RegisterDatabasePerTenant(
        IServiceCollection services)
    {
        services.AddMecesoftTenantData<AppDbContext, TenantConnectionStringResolver>(
            (options, connectionString) => options.UseSqlServer(connectionString));
        services.AddMecesoftRepository<AppDbContext, Order>();
    }

    public static void RegisterAuditing(
        IServiceCollection services,
        string connectionString)
    {
        services.AddMecesoftAuditing<CurrentUserProvider>();
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options.UseSqlServer(connectionString)
                .AddMecesoftAuditing(serviceProvider));
    }

    public static async Task LegacyRepositoryUsageAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var repository = new Repository<Order, AppDbContext>(context);

        IQueryable<Order> query = repository.GetAll(asNoTracking: true)
            .Include(order => order.Lines)
            .ThenInclude(line => line.Product)
            .Where(order => order.Total > 0);

        List<Order> orders = await query.ToListAsync(cancellationToken);

        Order? byExpression = await repository.GetByExpressionAsync(
            order => order.Number == "ORD-1",
            cancellationToken);

        string? customer = await repository.GetByExpressionDtoAsync(
            order => order.Number == "ORD-1",
            order => order.CustomerName,
            cancellationToken);

        bool exists = await repository.AnyAsync(
            order => order.Number == "ORD-1",
            cancellationToken);

        PageResult<Order> page = await repository.GetAll()
            .OrderBy(order => order.Number)
            .ToPageAsync(new PageRequest(1, 20), cancellationToken);

        PageResult<Order> stablePage = await repository.GetAll()
            .ToStablePageAsync(new PageRequest(1, 20), order => order.Id, cancellationToken);

        _ = orders;
        _ = byExpression;
        _ = customer;
        _ = exists;
        _ = page;
        _ = stablePage;
    }

    public static async Task UnitOfWorkUsageAsync(
        AppDbContext context,
        Order order,
        AuditEntry auditEntry,
        CancellationToken cancellationToken)
    {
        var repository = new Repository<Order, AppDbContext>(context);
        var unitOfWork = new UnitOfWork<AppDbContext>(context);

        await repository.AddAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await repository.AddAsync(order, token);
            await repository.AddAsync(order, token);
            _ = auditEntry;
        }, cancellationToken);
    }

    public static async Task DetachedRowVersionUpdateAsync(
        AppDbContext context,
        Order loadedOrder,
        CancellationToken cancellationToken)
    {
        var repository = new Repository<Order, AppDbContext>(context);
        var unitOfWork = new UnitOfWork<AppDbContext>(context);

        byte[] originalRowVersion = loadedOrder.RowVersion.ToArray();
        loadedOrder.Number = "ORD-2";
        repository.Update(loadedOrder, originalRowVersion);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }

    public static async Task SoftDeleteUsageAsync(
        AppDbContext context,
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        var softDelete = new SoftDeleteService<AppDbContext, Invoice, Guid>(
            context,
            invoice => invoice.Id);

        await softDelete.SoftDeleteAsync(invoiceId, cancellationToken);
        await softDelete.RestoreAsync(invoiceId, cancellationToken);

        var repository = new Repository<Invoice, AppDbContext>(context);
        List<Invoice> all = await repository
            .GetAll(includeDeleted: true)
            .ToListAsync(cancellationToken);
        _ = all;
    }
}

public sealed class Order : ISoftDeletable, IAuditable
{
    public Guid Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public decimal Total { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public List<OrderLine> Lines { get; } = [];
}

public sealed class OrderLine
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public Product? Product { get; set; }

    public Order? Order { get; set; }
}

public sealed class Product
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class Invoice : ISoftDeletable
{
    public Guid Id { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Number).IsRequired();
            entity.Property(order => order.RowVersion).IsRowVersion();
            entity.HasMany(order => order.Lines)
                .WithOne(line => line.Order)
                .HasForeignKey(line => line.OrderId);
        });
        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(line => line.Id);
            entity.HasOne(line => line.Product)
                .WithMany()
                .HasForeignKey(line => line.ProductId);
        });
        modelBuilder.Entity<Product>(entity => entity.HasKey(product => product.Id));
        modelBuilder.Entity<Invoice>(entity => entity.HasKey(invoice => invoice.Id));
        modelBuilder.Entity<AuditEntry>(entity => entity.HasKey(entry => entry.Id));
        modelBuilder.ApplyMecesoftSoftDeleteFilters();
    }
}

public sealed class AppIdentityDbContext : IdentityDbContext<IdentityUser>
{
    public AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
}

public interface ITenantContext
{
    string? ConnectionString { get; }
}

public sealed class TenantConnectionStringResolver(
    ITenantContext tenantContext) : ITenantConnectionStringResolver
{
    public string ResolveConnectionString()
    {
        return tenantContext.ConnectionString
            ?? throw new InvalidOperationException("No tenant is resolved.");
    }
}

public sealed class CurrentUserProvider : IAuditUserProvider
{
    public string? UserId => "sample-user";
}
