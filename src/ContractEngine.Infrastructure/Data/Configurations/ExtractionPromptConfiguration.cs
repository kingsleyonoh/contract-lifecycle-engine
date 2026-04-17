using System.Text.Json;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core configuration for <see cref="ExtractionPrompt"/> → <c>extraction_prompts</c> table.
/// PRD §4.11. Deliberately does NOT implement <c>ITenantScoped</c> — see entity class for rationale.
/// </summary>
internal sealed class ExtractionPromptConfiguration : IEntityTypeConfiguration<ExtractionPrompt>
{
    public void Configure(EntityTypeBuilder<ExtractionPrompt> entity)
    {
        entity.ToTable("extraction_prompts");
        entity.HasKey(p => p.Id);

        entity.Property(p => p.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(p => p.TenantId)
            .HasColumnName("tenant_id");
        // Nullable by design: null = system default, populated = tenant-specific override.

        entity.Property(p => p.PromptType)
            .HasColumnName("prompt_type")
            .HasColumnType("varchar(50)")
            .IsRequired();

        entity.Property(p => p.PromptText)
            .HasColumnName("prompt_text")
            .HasColumnType("text")
            .IsRequired();

        // JSONB response_schema — same pattern as Obligation.Metadata.
        var jsonOptions = new JsonSerializerOptions();
        entity.Property(p => p.ResponseSchema)
            .HasColumnName("response_schema")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, jsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, jsonOptions));

        entity.Property(p => p.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        entity.Property(p => p.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(p => p.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // Optional FK to Tenant when tenant_id is populated.
        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.11 UNIQUE (tenant_id, prompt_type). NULLS NOT DISTINCT is handled at migration
        // level (raw SQL) because EF Core doesn't expose this Postgres 15+ feature natively.
        // The EF-level index is declared here for the model snapshot; the migration overrides it
        // with raw SQL.
        entity.HasIndex(p => new { p.TenantId, p.PromptType })
            .IsUnique()
            .HasDatabaseName("ux_extraction_prompts_tenant_id_prompt_type");
    }
}
