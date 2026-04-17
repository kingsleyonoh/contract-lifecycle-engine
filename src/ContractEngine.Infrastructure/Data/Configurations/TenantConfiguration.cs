using System.Text.Json;
using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> entity)
    {
        entity.ToTable("tenants");
        entity.HasKey(t => t.Id);

        entity.Property(t => t.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(t => t.Name)
            .HasColumnName("name")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(t => t.ApiKeyHash)
            .HasColumnName("api_key_hash")
            .HasColumnType("varchar(512)")
            .IsRequired();

        entity.Property(t => t.ApiKeyPrefix)
            .HasColumnName("api_key_prefix")
            .HasColumnType("varchar(20)")
            .IsRequired();

        entity.Property(t => t.DefaultTimezone)
            .HasColumnName("default_timezone")
            .HasColumnType("varchar(50)")
            .HasDefaultValue("UTC");

        entity.Property(t => t.DefaultCurrency)
            .HasColumnName("default_currency")
            .HasColumnType("varchar(3)")
            .HasDefaultValue("USD");

        entity.Property(t => t.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true);

        entity.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        entity.Property(t => t.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // JSONB Metadata — serialise via System.Text.Json. Use ValueComparer with deep equality
        // so EF Core change-tracking behaves correctly on the dictionary.
        var jsonOptions = new JsonSerializerOptions();
        entity.Property(t => t.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, jsonOptions),
                v => string.IsNullOrEmpty(v)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(v, jsonOptions));

        entity.HasIndex(t => t.ApiKeyHash)
            .IsUnique()
            .HasDatabaseName("ix_tenants_api_key_hash");
    }
}
