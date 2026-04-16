using System.Linq.Expressions;
using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// Primary EF Core <see cref="DbContext"/> for the Contract Lifecycle Engine. Injects the
/// request-scoped <see cref="ITenantContext"/> so that entities implementing
/// <see cref="ITenantScoped"/> automatically receive a global query filter restricting them to
/// the current tenant (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §4).
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

        ConfigureTenant(modelBuilder);
        ConfigureCounterparty(modelBuilder);
    }

    private void ConfigureCounterparty(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Counterparty>();
        entity.ToTable("counterparties");
        entity.HasKey(c => c.Id);

        entity.Property(c => c.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(c => c.Name)
            .HasColumnName("name")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(c => c.LegalName)
            .HasColumnName("legal_name")
            .HasColumnType("varchar(255)");

        entity.Property(c => c.Industry)
            .HasColumnName("industry")
            .HasColumnType("varchar(100)");

        entity.Property(c => c.ContactEmail)
            .HasColumnName("contact_email")
            .HasColumnType("varchar(255)");

        entity.Property(c => c.ContactName)
            .HasColumnName("contact_name")
            .HasColumnType("varchar(255)");

        entity.Property(c => c.Notes)
            .HasColumnName("notes")
            .HasColumnType("text");

        entity.Property(c => c.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(c => c.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => new { c.TenantId, c.Name })
            .HasDatabaseName("ix_counterparties_tenant_id_name");

        ApplyTenantQueryFilter<Counterparty>(modelBuilder);
    }

    private static void ConfigureTenant(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Tenant>();
        entity.ToTable("tenants");
        entity.HasKey(t => t.Id);

        entity.Property(t => t.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(t => t.Name)
            .HasColumnName("name")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(t => t.ApiKeyHash)
            .HasColumnName("api_key_hash")
            .HasColumnType("varchar(512)")
            .IsRequired();

        entity.Property(t => t.ApiKeyPrefix)
            .HasColumnName("api_key_prefix")
            .HasColumnType("varchar(20)")
            .IsRequired();

        entity.Property(t => t.DefaultTimezone)
            .HasColumnName("default_timezone")
            .HasColumnType("varchar(50)")
            .HasDefaultValue("UTC");

        entity.Property(t => t.DefaultCurrency)
            .HasColumnName("default_currency")
            .HasColumnType("varchar(3)")
            .HasDefaultValue("USD");

        entity.Property(t => t.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        entity.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(t => t.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // JSONB Metadata — serialise via System.Text.Json. Use ValueComparer with deep equality
        // so EF Core change-tracking behaves correctly on the dictionary.
        var jsonOptions = new JsonSerializerOptions();
        entity.Property(t => t.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, jsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, jsonOptions));

        entity.HasIndex(t => t.ApiKeyHash)
            .IsUnique()
            .HasDatabaseName("ix_tenants_api_key_hash");
    }
}
