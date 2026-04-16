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

        ConfigureTenant(modelBuilder);
        ConfigureCounterparty(modelBuilder);
        ConfigureContract(modelBuilder);
        ConfigureContractDocument(modelBuilder);
        ConfigureContractTag(modelBuilder);
        ConfigureContractVersion(modelBuilder);
        ConfigureObligation(modelBuilder);
        ConfigureObligationEvent(modelBuilder);
        ConfigureHolidayCalendar(modelBuilder);
        ConfigureDeadlineAlert(modelBuilder);
    }

    private void ConfigureDeadlineAlert(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<DeadlineAlert>();
        entity.ToTable("deadline_alerts");
        entity.HasKey(a => a.Id);

        entity.Property(a => a.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(a => a.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(a => a.ObligationId)
            .HasColumnName("obligation_id")
            .IsRequired();

        entity.Property(a => a.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        // Enum → snake_case lowercase string (matches PRD §4.9 CHECK constraint values).
        entity.Property(a => a.AlertType)
            .HasColumnName("alert_type")
            .HasColumnType("varchar(50)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ContractEngine.Core.Enums.AlertType>(v))
            .IsRequired();

        entity.Property(a => a.DaysRemaining)
            .HasColumnName("days_remaining");

        entity.Property(a => a.Message)
            .HasColumnName("message")
            .HasColumnType("text")
            .IsRequired();

        entity.Property(a => a.Acknowledged)
            .HasColumnName("acknowledged")
            .HasDefaultValue(false);

        entity.Property(a => a.AcknowledgedAt)
            .HasColumnName("acknowledged_at")
            .HasColumnType("timestamptz");

        entity.Property(a => a.AcknowledgedBy)
            .HasColumnName("acknowledged_by")
            .HasColumnType("varchar(255)");

        entity.Property(a => a.NotificationSent)
            .HasColumnName("notification_sent")
            .HasDefaultValue(false);

        entity.Property(a => a.NotificationSentAt)
            .HasColumnName("notification_sent_at")
            .HasColumnType("timestamptz");

        entity.Property(a => a.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.9: alerts are audit / notification history. Restrict on parent deletes so a
        // seed-cleanup slip that blindly drops an obligation fails loudly instead of silently
        // orphaning (or worse, losing) the alert trail. Production never hard-deletes obligations.
        entity.HasOne<Obligation>()
            .WithMany()
            .HasForeignKey(a => a.ObligationId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(a => a.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        // PRD §4.9 indexes — (tenant_id, acknowledged, created_at DESC) is the hot path for
        // "unacknowledged alerts" lookups; Postgres can still use it for the asc/desc variants so
        // we rely on the default storage order and let the query planner decide.
        entity.HasIndex(a => new { a.TenantId, a.Acknowledged, a.CreatedAt })
            .HasDatabaseName("ix_deadline_alerts_tenant_id_acknowledged_created_at");
        entity.HasIndex(a => new { a.TenantId, a.ObligationId })
            .HasDatabaseName("ix_deadline_alerts_tenant_id_obligation_id");

        ApplyTenantQueryFilter<DeadlineAlert>(modelBuilder);
    }

    private static void ConfigureHolidayCalendar(ModelBuilder modelBuilder)
    {
        // NOTE: HolidayCalendar deliberately does NOT implement ITenantScoped and therefore does NOT
        // receive a tenant query filter — rows with tenant_id = NULL (system-wide) must be visible
        // to every tenant. The repository layer enforces tenant isolation by filtering on
        // tenant_id = @id OR tenant_id IS NULL and merging in-memory.
        var entity = modelBuilder.Entity<HolidayCalendar>();
        entity.ToTable("holiday_calendars");
        entity.HasKey(h => h.Id);

        entity.Property(h => h.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(h => h.TenantId)
            .HasColumnName("tenant_id");
        // Nullable by design: null = system-wide, populated = tenant-specific override.

        entity.Property(h => h.CalendarCode)
            .HasColumnName("calendar_code")
            .HasColumnType("varchar(50)")
            .IsRequired();

        entity.Property(h => h.Year)
            .HasColumnName("year")
            .IsRequired();

        entity.Property(h => h.HolidayDate)
            .HasColumnName("holiday_date")
            .HasColumnType("date")
            .IsRequired();

        entity.Property(h => h.HolidayName)
            .HasColumnName("holiday_name")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(h => h.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // Optional FK to Tenant when tenant_id is populated. EF's HasOne on a nullable scalar FK
        // builds an optional relationship automatically.
        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(h => h.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.10 UNIQUE (tenant_id, calendar_code, holiday_date). Postgres treats NULLs as
        // distinct by default — that's fine here: two system-wide rows with tenant_id = NULL for
        // the same (code, date) pair are still a duplicate because the UNIQUE index treats them as
        // such only if NULLS NOT DISTINCT is set. We set that at the migration level.
        entity.HasIndex(h => new { h.TenantId, h.CalendarCode, h.HolidayDate })
            .IsUnique()
            .HasDatabaseName("ux_holiday_calendars_tenant_id_calendar_code_holiday_date");

        // Hot lookup by (code, year, date).
        entity.HasIndex(h => new { h.CalendarCode, h.Year, h.HolidayDate })
            .HasDatabaseName("ix_holiday_calendars_calendar_code_year_holiday_date");

        // Secondary lookup for "find all holidays for tenant".
        entity.HasIndex(h => new { h.TenantId, h.CalendarCode })
            .HasDatabaseName("ix_holiday_calendars_tenant_id_calendar_code");
    }

    private void ConfigureObligation(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<Obligation>();
        entity.ToTable("obligations");
        entity.HasKey(o => o.Id);

        entity.Property(o => o.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(o => o.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(o => o.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        // Enum → lowercase snake_case string (matches PRD §4.6 CHECK constraint values).
        entity.Property(o => o.ObligationType)
            .HasColumnName("obligation_type")
            .HasColumnType("varchar(50)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ObligationType>(v))
            .IsRequired();

        entity.Property(o => o.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ObligationStatus>(v))
            .HasDefaultValue(ObligationStatus.Pending)
            .IsRequired();

        entity.Property(o => o.Title)
            .HasColumnName("title")
            .HasColumnType("varchar(500)")
            .IsRequired();

        entity.Property(o => o.Description)
            .HasColumnName("description")
            .HasColumnType("text");

        entity.Property(o => o.ResponsibleParty)
            .HasColumnName("responsible_party")
            .HasColumnType("varchar(50)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ResponsibleParty>(v))
            .HasDefaultValue(ResponsibleParty.Us);

        entity.Property(o => o.DeadlineDate)
            .HasColumnName("deadline_date")
            .HasColumnType("date");

        entity.Property(o => o.DeadlineFormula)
            .HasColumnName("deadline_formula")
            .HasColumnType("varchar(255)");

        entity.Property(o => o.Recurrence)
            .HasColumnName("recurrence")
            .HasColumnType("varchar(50)")
            .HasConversion(
                v => v == null ? null : EnumToSnake(v.Value.ToString()),
                v => string.IsNullOrEmpty(v) ? null : (ObligationRecurrence?)ParseEnum<ObligationRecurrence>(v));

        entity.Property(o => o.NextDueDate)
            .HasColumnName("next_due_date")
            .HasColumnType("date");

        entity.Property(o => o.Amount)
            .HasColumnName("amount")
            .HasColumnType("decimal(15,2)");

        entity.Property(o => o.Currency)
            .HasColumnName("currency")
            .HasColumnType("varchar(3)")
            .HasDefaultValue("USD");

        entity.Property(o => o.AlertWindowDays)
            .HasColumnName("alert_window_days")
            .HasDefaultValue(30);

        entity.Property(o => o.GracePeriodDays)
            .HasColumnName("grace_period_days")
            .HasDefaultValue(0);

        entity.Property(o => o.BusinessDayCalendar)
            .HasColumnName("business_day_calendar")
            .HasColumnType("varchar(50)")
            .HasDefaultValue("US");

        entity.Property(o => o.Source)
            .HasColumnName("source")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumToSnake(v.ToString()),
                v => ParseEnum<ObligationSource>(v))
            .HasDefaultValue(ObligationSource.Manual);

        // ExtractionJobId: column only — no FK relationship declared because extraction_jobs
        // doesn't exist yet (Phase 2). A later migration will add the FK via AddForeignKey once
        // that table lands. Today the column is a nullable uuid with no referential integrity.
        entity.Property(o => o.ExtractionJobId)
            .HasColumnName("extraction_job_id")
            .HasColumnType("uuid");

        entity.Property(o => o.ConfidenceScore)
            .HasColumnName("confidence_score")
            .HasColumnType("decimal(3,2)");

        entity.Property(o => o.ClauseReference)
            .HasColumnName("clause_reference")
            .HasColumnType("varchar(255)");

        // JSONB metadata — same pattern as Contract.Metadata.
        var metadataJsonOptions = new JsonSerializerOptions();
        entity.Property(o => o.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, metadataJsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, metadataJsonOptions));

        entity.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(o => o.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(o => o.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §5.1: archiving a contract cascades by state ("→ expired"), not by row delete.
        // Use Restrict so a hard contract-delete fails loudly if obligations are still attached;
        // production never hard-deletes contracts, but tests and seed data benefit from the guard.
        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(o => o.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        // PRD §4.6 indexes. ix_obligations_tenant_id_next_due_date is the hot path for
        // DeadlineScannerJob (Phase 2) so we name it explicitly for query-plan reviews.
        entity.HasIndex(o => new { o.TenantId, o.Status })
            .HasDatabaseName("ix_obligations_tenant_id_status");
        entity.HasIndex(o => new { o.TenantId, o.ContractId })
            .HasDatabaseName("ix_obligations_tenant_id_contract_id");
        entity.HasIndex(o => new { o.TenantId, o.NextDueDate })
            .HasDatabaseName("ix_obligations_tenant_id_next_due_date");
        entity.HasIndex(o => new { o.TenantId, o.ObligationType })
            .HasDatabaseName("ix_obligations_tenant_id_obligation_type");

        ApplyTenantQueryFilter<Obligation>(modelBuilder);
    }

    private void ConfigureObligationEvent(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ObligationEvent>();
        entity.ToTable("obligation_events");
        entity.HasKey(e => e.Id);

        entity.Property(e => e.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(e => e.ObligationId)
            .HasColumnName("obligation_id")
            .IsRequired();

        entity.Property(e => e.FromStatus)
            .HasColumnName("from_status")
            .HasColumnType("varchar(20)")
            .IsRequired();

        entity.Property(e => e.ToStatus)
            .HasColumnName("to_status")
            .HasColumnType("varchar(20)")
            .IsRequired();

        entity.Property(e => e.Actor)
            .HasColumnName("actor")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasColumnType("text");

        var metadataJsonOptions = new JsonSerializerOptions();
        entity.Property(e => e.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, metadataJsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, metadataJsonOptions));

        entity.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Events cascade with their owning obligation — if we ever hard-delete an obligation (test
        // cleanup, not prod), the event rows follow so no orphans.
        entity.HasOne<Obligation>()
            .WithMany()
            .HasForeignKey(e => e.ObligationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.TenantId, e.ObligationId, e.CreatedAt })
            .HasDatabaseName("ix_obligation_events_tenant_id_obligation_id_created_at");

        ApplyTenantQueryFilter<ObligationEvent>(modelBuilder);
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
