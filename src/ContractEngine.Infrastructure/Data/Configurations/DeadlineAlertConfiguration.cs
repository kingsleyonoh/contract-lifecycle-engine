using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class DeadlineAlertConfiguration : IEntityTypeConfiguration<DeadlineAlert>
{
    public void Configure(EntityTypeBuilder<DeadlineAlert> entity)
    {
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
                v => EnumStringConversions.EnumToSnake(v.ToString()),
                v => EnumStringConversions.ParseEnum<AlertType>(v))
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
    }
}
