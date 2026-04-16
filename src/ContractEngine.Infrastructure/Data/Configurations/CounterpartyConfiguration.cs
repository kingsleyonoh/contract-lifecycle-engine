using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class CounterpartyConfiguration : IEntityTypeConfiguration<Counterparty>
{
    public void Configure(EntityTypeBuilder<Counterparty> entity)
    {
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
    }
}
