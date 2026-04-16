using System.Linq.Expressions;
using System.Text.Json;
using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
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

    public DbSet<Contract> Contracts => Set<Contract>();

    public DbSet<ContractDocument> ContractDocuments => Set<ContractDocument>();

    public DbSet<ContractTag> ContractTags => Set<ContractTag>();

    public DbSet<ContractVersion> ContractVersions => Set<ContractVersion>();

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
        ConfigureContract(modelBuilder);
        ConfigureContractDocument(modelBuilder);
        ConfigureContractTag(modelBuilder);
        ConfigureContractVersion(modelBuilder);
    }

    private void ConfigureContractTag(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ContractTag>();
        entity.ToTable("contract_tags");
        entity.HasKey(t => t.Id);

        entity.Property(t => t.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(t => t.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(t => t.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        entity.Property(t => t.Tag)
            .HasColumnName("tag")
            .HasColumnType("varchar(100)")
            .IsRequired();

        entity.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(t => t.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.12 — UNIQUE (tenant_id, contract_id, tag) prevents duplicate labels on a contract.
        entity.HasIndex(t => new { t.TenantId, t.ContractId, t.Tag })
            .IsUnique()
            .HasDatabaseName("ux_contract_tags_tenant_id_contract_id_tag");

        // Secondary index for "find contracts by tag" lookups.
        entity.HasIndex(t => new { t.TenantId, t.Tag })
            .HasDatabaseName("ix_contract_tags_tenant_id_tag");

        ApplyTenantQueryFilter<ContractTag>(modelBuilder);
    }

    private void ConfigureContractVersion(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ContractVersion>();
        entity.ToTable("contract_versions");
        entity.HasKey(v => v.Id);

        entity.Property(v => v.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(v => v.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(v => v.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        entity.Property(v => v.VersionNumber)
            .HasColumnName("version_number")
            .IsRequired();

        entity.Property(v => v.ChangeSummary)
            .HasColumnName("change_summary")
            .HasColumnType("text");

        // JSONB diff_result — same serialisation pattern as Contract.Metadata. Null until the
        // Phase 2 diff service populates it.
        var diffJsonOptions = new JsonSerializerOptions();
        entity.Property(v => v.DiffResult)
            .HasColumnName("diff_result")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, diffJsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, diffJsonOptions));

        entity.Property(v => v.EffectiveDate)
            .HasColumnName("effective_date")
            .HasColumnType("date");

        entity.Property(v => v.CreatedBy)
            .HasColumnName("created_by")
            .HasColumnType("varchar(255)");

        entity.Property(v => v.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(v => v.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(v => v.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.4 — UNIQUE (contract_id, version_number) enforces monotonic version numbering.
        entity.HasIndex(v => new { v.ContractId, v.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ux_contract_versions_contract_id_version_number");

        // Lookup index for paginated history queries.
        entity.HasIndex(v => new { v.TenantId, v.ContractId, v.VersionNumber })
            .HasDatabaseName("ix_contract_versions_tenant_id_contract_id_version_number");

        ApplyTenantQueryFilter<ContractVersion>(modelBuilder);
    }

    private void ConfigureContractDocument(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ContractDocument>();
        entity.ToTable("contract_documents");
        entity.HasKey(d => d.Id);

        entity.Property(d => d.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(d => d.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        entity.Property(d => d.VersionNumber)
            .HasColumnName("version_number");

        entity.Property(d => d.FileName)
            .HasColumnName("file_name")
            .HasColumnType("varchar(500)")
            .IsRequired();

        entity.Property(d => d.FilePath)
            .HasColumnName("file_path")
            .HasColumnType("varchar(1000)")
            .IsRequired();

        entity.Property(d => d.FileSizeBytes)
            .HasColumnName("file_size_bytes");

        entity.Property(d => d.MimeType)
            .HasColumnName("mime_type")
            .HasColumnType("varchar(100)");

        entity.Property(d => d.RagDocumentId)
            .HasColumnName("rag_document_id")
            .HasColumnType("varchar(255)");

        entity.Property(d => d.CreatedAt)
            .HasColumnName("uploaded_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(d => d.UploadedBy)
            .HasColumnName("uploaded_by")
            .HasColumnType("varchar(255)");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(d => d.ContractId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(d => new { d.TenantId, d.ContractId })
            .HasDatabaseName("ix_contract_documents_tenant_id_contract_id");

        ApplyTenantQueryFilter<ContractDocument>(modelBuilder);
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

    private void ConfigureContract(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Contract>();
        entity.ToTable("contracts");
        entity.HasKey(c => c.Id);

        entity.Property(c => c.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(c => c.CounterpartyId)
            .HasColumnName("counterparty_id")
            .IsRequired();

        entity.Property(c => c.Title)
            .HasColumnName("title")
            .HasColumnType("varchar(500)")
            .IsRequired();

        entity.Property(c => c.ReferenceNumber)
            .HasColumnName("reference_number")
            .HasColumnType("varchar(100)");

        // Enum → lowercase snake_case string (matches PRD §4.3 CHECK constraint values).
        entity.Property(c => c.ContractType)
            .HasColumnName("contract_type")
            .HasColumnType("varchar(50)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ContractType>(v))
            .IsRequired();

        entity.Property(c => c.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ContractStatus>(v))
            .HasDefaultValue(ContractStatus.Draft)
            .IsRequired();

        entity.Property(c => c.EffectiveDate)
            .HasColumnName("effective_date")
            .HasColumnType("date");

        entity.Property(c => c.EndDate)
            .HasColumnName("end_date")
            .HasColumnType("date");

        entity.Property(c => c.RenewalNoticeDays)
            .HasColumnName("renewal_notice_days")
            .HasDefaultValue(90);

        entity.Property(c => c.AutoRenewal)
            .HasColumnName("auto_renewal")
            .HasDefaultValue(false);

        entity.Property(c => c.AutoRenewalPeriodMonths)
            .HasColumnName("auto_renewal_period_months");

        entity.Property(c => c.TotalValue)
            .HasColumnName("total_value")
            .HasColumnType("decimal(15,2)");

        entity.Property(c => c.Currency)
            .HasColumnName("currency")
            .HasColumnType("varchar(3)")
            .HasDefaultValue("USD");

        entity.Property(c => c.GoverningLaw)
            .HasColumnName("governing_law")
            .HasColumnType("varchar(100)");

        // JSONB metadata — same pattern as Tenant.Metadata.
        var metadataJsonOptions = new JsonSerializerOptions();
        entity.Property(c => c.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, metadataJsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, metadataJsonOptions));

        entity.Property(c => c.RagDocumentId)
            .HasColumnName("rag_document_id")
            .HasColumnType("varchar(255)");

        entity.Property(c => c.CurrentVersion)
            .HasColumnName("current_version")
            .HasDefaultValue(1);

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

        entity.HasOne<Counterparty>()
            .WithMany()
            .HasForeignKey(c => c.CounterpartyId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(c => new { c.TenantId, c.Status })
            .HasDatabaseName("ix_contracts_tenant_id_status");
        entity.HasIndex(c => new { c.TenantId, c.CounterpartyId })
            .HasDatabaseName("ix_contracts_tenant_id_counterparty_id");
        entity.HasIndex(c => new { c.TenantId, c.EndDate })
            .HasDatabaseName("ix_contracts_tenant_id_end_date");
        entity.HasIndex(c => new { c.TenantId, c.ReferenceNumber })
            .HasDatabaseName("ix_contracts_tenant_id_reference_number");

        ApplyTenantQueryFilter<Contract>(modelBuilder);
    }

    private static string EnumToSnake(string value)
    {
        // Mirrors JsonNamingPolicy.SnakeCaseLower: PascalCase → snake_case (lowercase).
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        // Tolerates either "active" (DB) or "Active" (legacy) thanks to ignoreCase = true. We
        // strip underscores before Enum.Parse so "termination_notice" → "TerminationNotice".
        var normalized = value.Replace("_", string.Empty);
        return Enum.Parse<TEnum>(normalized, ignoreCase: true);
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
