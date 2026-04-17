using System.Text.Json;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ObligationConfiguration : IEntityTypeConfiguration<Obligation>
{
    public void Configure(EntityTypeBuilder<Obligation> entity)
    {
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
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ObligationType>(v))
            .IsRequired();

        entity.Property(o => o.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ObligationStatus>(v))
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
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ResponsibleParty>(v))
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
                v => v == null ? null : EnumStringConversions.EnumToSnake(v.Value.ToString()),
                v => string.IsNullOrEmpty(v) ? null : (ObligationRecurrence?)EnumStringConversions.ParseEnum<ObligationRecurrence>(v));

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
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ObligationSource>(v))
            .HasDefaultValue(ObligationSource.Manual);

        // ExtractionJobId: FK to extraction_jobs(id) ON DELETE SET NULL. The extraction_jobs table
        // landed in Batch 020; the FK was added by the same migration. The column remains nullable
        // (null for manual obligations, populated for RAG-extracted ones).
        entity.Property(o => o.ExtractionJobId)
            .HasColumnName("extraction_job_id")
            .HasColumnType("uuid");

        entity.HasOne<ExtractionJob>()
            .WithMany()
            .HasForeignKey(o => o.ExtractionJobId)
            .OnDelete(DeleteBehavior.SetNull);

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
    }
}
