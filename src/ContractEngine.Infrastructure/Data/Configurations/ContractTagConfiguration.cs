using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ContractTagConfiguration : IEntityTypeConfiguration<ContractTag>
{
    public void Configure(EntityTypeBuilder<ContractTag> entity)
    {
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
    }
}
