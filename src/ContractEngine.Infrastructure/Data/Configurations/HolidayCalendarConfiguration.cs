using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContractEngine.Infrastructure.Data.Configurations;

internal sealed class HolidayCalendarConfiguration : IEntityTypeConfiguration<HolidayCalendar>
{
    public void Configure(EntityTypeBuilder<HolidayCalendar> entity)
    {
        // NOTE: HolidayCalendar deliberately does NOT implement ITenantScoped and therefore does NOT
        // receive a tenant query filter — rows with tenant_id = NULL (system-wide) must be visible
        // to every tenant. The repository layer enforces tenant isolation by filtering on
        // tenant_id = @id OR tenant_id IS NULL and merging in-memory.
        entity.ToTable("holiday_calendars");
        entity.HasKey(h => h.Id);

        entity.Property(h => h.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(h => h.TenantId)
            .HasColumnName("tenant_id");
        // Nullable by design: null = system-wide, populated = tenant-specific override.

        entity.Property(h => h.CalendarCode)
            .HasColumnName("calendar_code")
            .HasColumnType("varchar(50)")
            .IsRequired();

        entity.Property(h => h.Year)
            .HasColumnName("year")
            .IsRequired();

        entity.Property(h => h.HolidayDate)
            .HasColumnName("holiday_date")
            .HasColumnType("date")
            .IsRequired();

        entity.Property(h => h.HolidayName)
            .HasColumnName("holiday_name")
            .HasColumnType("varchar(255)")
            .IsRequired();

        entity.Property(h => h.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // Optional FK to Tenant when tenant_id is populated. EF's HasOne on a nullable scalar FK
        // builds an optional relationship automatically.
        entity.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(h => h.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // PRD §4.10 UNIQUE (tenant_id, calendar_code, holiday_date). Postgres treats NULLs as
        // distinct by default — that's fine here: two system-wide rows with tenant_id = NULL for
        // the same (code, date) pair are still a duplicate because the UNIQUE index treats them as
        // such only if NULLS NOT DISTINCT is set. We set that at the migration level.
        entity.HasIndex(h => new { h.TenantId, h.CalendarCode, h.HolidayDate })
            .IsUnique()
            .HasDatabaseName("ux_holiday_calendars_tenant_id_calendar_code_holiday_date");

        // Hot lookup by (code, year, date).
        entity.HasIndex(h => new { h.CalendarCode, h.Year, h.HolidayDate })
            .HasDatabaseName("ix_holiday_calendars_calendar_code_year_holiday_date");

        // Secondary lookup for "find all holidays for tenant".
        entity.HasIndex(h => new { h.TenantId, h.CalendarCode })
            .HasDatabaseName("ix_holiday_calendars_tenant_id_calendar_code");
    }
}
