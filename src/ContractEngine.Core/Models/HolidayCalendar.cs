namespace ContractEngine.Core.Models;

/// <summary>
/// HolidayCalendar — a single (country, year, date) row defining a public holiday. PRD §4.10 defines
/// the schema: rows with <c>tenant_id = null</c> are SYSTEM-WIDE (seeded on bootstrap for US, DE, UK,
/// NL + current year + next year); rows with <c>tenant_id</c> populated are TENANT-SPECIFIC OVERRIDES
/// layered on top of the system calendar for that tenant.
///
/// <para><b>Why this entity does NOT implement <c>ITenantScoped</c>:</b> the global query filter in
/// <c>ContractDbContext</c> requires <c>TenantId == current</c>, which would hide every system-wide
/// row (tenant_id = null) from every query. Holiday lookups deliberately need to see both — the
/// repository layer joins them explicitly via <c>WHERE tenant_id = @id OR tenant_id IS NULL</c> and
/// merges in memory (tenant row wins on duplicate date). Isolation of tenant-specific holidays is
/// still enforced at the repository layer: no tenant can query another tenant's custom rows.</para>
///
/// <para>Unique constraint <c>(tenant_id, calendar_code, holiday_date)</c> — a tenant cannot have
/// two overrides for the same date. Null tenant_id rows are unique on (code, date) too.</para>
/// </summary>
public class HolidayCalendar
{
    public Guid Id { get; set; }

    /// <summary>Null for system-wide rows (seeded), set for tenant-specific overrides.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>ISO-ish country code: <c>US</c>, <c>DE</c>, <c>UK</c>, <c>NL</c> today.</summary>
    public string CalendarCode { get; set; } = string.Empty;

    /// <summary>Calendar year — denormalised for cheap (code, year) lookups in the calculator cache.</summary>
    public int Year { get; set; }

    public DateOnly HolidayDate { get; set; }

    public string HolidayName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
