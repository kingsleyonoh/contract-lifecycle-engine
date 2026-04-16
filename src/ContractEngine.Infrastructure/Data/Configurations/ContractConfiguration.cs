using System.Text.Json;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> entity)
    {
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
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ContractType>(v))
            .IsRequired();

        entity.Property(c => c.Status)
            .HasColumnName("status")
            .HasColumnType("varchar(20)")
            .HasConversion(
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<ContractStatus>(v))
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
    }
}
