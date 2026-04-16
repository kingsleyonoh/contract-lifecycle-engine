using System.Text.Json;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ObligationEventConfiguration : IEntityTypeConfiguration<ObligationEvent>
{
    public void Configure(EntityTypeBuilder<ObligationEvent> entity)
    {
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
    }
}
