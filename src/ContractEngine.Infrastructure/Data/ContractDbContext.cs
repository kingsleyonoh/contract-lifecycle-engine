using System.Linq.Expressions;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// Primary EF Core <see cref="DbContext"/> for the Contract Lifecycle Engine. Injects the
/// request-scoped <see cref="ITenantContext"/> so that entities implementing
/// <see cref="ITenantScoped"/> automatically receive a global query filter restricting them to
/// the current tenant (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §4).
///
/// <para>Entity mapping lives in one <see cref="IEntityTypeConfiguration{T}"/> class per entity
/// under <c>Data/Configurations/</c> — those are picked up via <c>ApplyConfigurationsFromAssembly</c>
/// in <see cref="OnModelCreating"/>. The ONLY thing the DbContext itself still owns is the tenant
/// query filter wiring, because that needs the request-scoped <see cref="ITenantContext"/> the
/// configuration classes don't have access to.</para>
/// </summary>
public class ContractDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public ContractDbContext(DbContextOptions<ContractDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Counterparty> Counterparties => Set<Counterparty>();

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<ContractDocument> ContractDocuments => Set<ContractDocument>();

    public DbSet<ContractTag> ContractTags => Set<ContractTag>();

    public DbSet<ContractVersion> ContractVersions => Set<ContractVersion>();

    public DbSet<Obligation> Obligations => Set<Obligation>();

    public DbSet<ObligationEvent> ObligationEvents => Set<ObligationEvent>();

    public DbSet<HolidayCalendar> HolidayCalendars => Set<HolidayCalendar>();

    public DbSet<DeadlineAlert> DeadlineAlerts => Set<DeadlineAlert>();

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

        // Pick up every IEntityTypeConfiguration<T> in this assembly (Data/Configurations/*).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ContractDbContext).Assembly);

        // Tenant query filters stay here because they need the scoped ITenantContext. Tenant and
        // HolidayCalendar are intentionally unfiltered (see their configuration classes).
        ApplyTenantQueryFilter<Counterparty>(modelBuilder);
        ApplyTenantQueryFilter<Contract>(modelBuilder);
        ApplyTenantQueryFilter<ContractDocument>(modelBuilder);
        ApplyTenantQueryFilter<ContractTag>(modelBuilder);
        ApplyTenantQueryFilter<ContractVersion>(modelBuilder);
        ApplyTenantQueryFilter<Obligation>(modelBuilder);
        ApplyTenantQueryFilter<ObligationEvent>(modelBuilder);
        ApplyTenantQueryFilter<DeadlineAlert>(modelBuilder);
    }
}
