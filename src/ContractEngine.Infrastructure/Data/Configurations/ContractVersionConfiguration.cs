using System.Text.Json;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ContractVersionConfiguration : IEntityTypeConfiguration<ContractVersion>
{
    public void Configure(EntityTypeBuilder<ContractVersion> entity)
    {
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
    }
}
