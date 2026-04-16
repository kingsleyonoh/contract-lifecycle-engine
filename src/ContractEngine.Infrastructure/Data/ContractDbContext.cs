using System.Linq.Expressions;
using ContractEngine.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// Primary EF Core <see cref="DbContext"/> for the Contract Lifecycle Engine. Injects the
/// request-scoped <see cref="ITenantContext"/> so that entities implementing
/// <see cref="ITenantScoped"/> automatically receive a global query filter restricting them to
/// the current tenant (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §4).
///
/// No entity configurations are registered in this batch (Phase 0). Subsequent batches will add
/// <c>DbSet&lt;T&gt;</c> properties and Fluent API configuration; each tenant-scoped entity must
/// be wired up via <see cref="ApplyTenantQueryFilter{TEntity}"/> inside
/// <see cref="OnModelCreating"/>.
/// </summary>
public class ContractDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public ContractDbContext(DbContextOptions<ContractDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Registers a tenant-scoped global query filter on the supplied entity type. Call from
    /// <see cref="OnModelCreating"/> for every entity that implements <see cref="ITenantScoped"/>.
    /// </summary>
    protected void ApplyTenantQueryFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = entity =>
            _tenantContext.TenantId != null && entity.TenantId == _tenantContext.TenantId;
        builder.Entity<TEntity>().HasQueryFilter(filter);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Entity configurations will be added in subsequent batches. Each tenant-scoped entity
        // must call ApplyTenantQueryFilter<T>(modelBuilder) here.
    }
}
