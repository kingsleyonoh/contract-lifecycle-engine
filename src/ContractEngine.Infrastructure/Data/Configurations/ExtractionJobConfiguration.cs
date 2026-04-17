using System.Text.Json;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="ExtractionJob"/> → <c>extraction_jobs</c> table.
/// PRD §4.8. Implements <c>ITenantScoped</c> — global query filter applies.
/// </summary>
internal sealed class ExtractionJobConfiguration : IEntityTypeConfiguration<ExtractionJob>
{
    public void Configure(EntityTypeBuilder<ExtractionJob> entity)
    {
        entity.ToTable("extraction_jobs");
        entity.HasKey(j => j.Id);

        entity.Property(j => j.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(j => j.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        entity.Property(j => j.ContractId)
            .HasColumnName("contract_id")
            .IsRequired();

        entity.Property(j => j.DocumentId)
            .HasColumnName("document_id");
        // Nullable — extraction may be contract-level without a specific document.

        // Enum → lowercase snake_case string.
        entity.Property(j => j.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ExtractionStatus>(v))
            .HasDefaultValue(ExtractionStatus.Queued)
            .IsRequired();

        // TEXT[] — Npgsql maps string[] natively to PostgreSQL text[].
        entity.Property(j => j.PromptTypes)
            .HasColumnName("prompt_types")
            .HasColumnType("text[]")
            .IsRequired();

        entity.Property(j => j.ObligationsFound)
            .HasColumnName("obligations_found")
            .HasDefaultValue(0);

        entity.Property(j => j.ObligationsConfirmed)
            .HasColumnName("obligations_confirmed")
            .HasDefaultValue(0);

        entity.Property(j => j.ErrorMessage)
            .HasColumnName("error_message")
            .HasColumnType("text");

        entity.Property(j => j.RagDocumentId)
            .HasColumnName("rag_document_id")
            .HasColumnType("varchar(255)");

        // JSONB raw_responses — same pattern as Obligation.Metadata.
        var jsonOptions = new JsonSerializerOptions();
        entity.Property(j => j.RawResponses)
            .HasColumnName("raw_responses")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, jsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, jsonOptions));

        entity.Property(j => j.StartedAt)
            .HasColumnName("started_at")
            .HasColumnType("timestamptz");

        entity.Property(j => j.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamptz");

        entity.Property(j => j.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(j => j.RetryCount)
            .HasColumnName("retry_count")
            .HasDefaultValue(0);

        // FKs
        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(j => j.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Contract>()
            .WithMany()
            .HasForeignKey(j => j.ContractId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<ContractDocument>()
            .WithMany()
            .HasForeignKey(j => j.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);

        // PRD §4.8 indexes
        entity.HasIndex(j => new { j.TenantId, j.Status })
            .HasDatabaseName("ix_extraction_jobs_tenant_id_status");
        entity.HasIndex(j => new { j.TenantId, j.ContractId })
            .HasDatabaseName("ix_extraction_jobs_tenant_id_contract_id");
    }
}
