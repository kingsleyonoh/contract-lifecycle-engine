using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class ContractDocumentConfiguration : IEntityTypeConfiguration<ContractDocument>
{
    public void Configure(EntityTypeBuilder<ContractDocument> entity)
    {
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
    }
}
