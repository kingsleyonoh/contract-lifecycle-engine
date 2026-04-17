using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>holiday_calendars</c> table. Unlike most tenant-scoped repositories, this
/// one takes an explicit nullable tenant id because system-wide rows (<c>tenant_id IS NULL</c>) must
/// be visible to every tenant. See <see cref="GetForCalendarAsync"/> for the merge semantics.
/// </summary>
public interface IHolidayCalendarRepository
{
    /// <summary>
    /// Returns the effective holiday set for <paramref name="calendarCode"/> in
    /// <paramref name="year"/> for <paramref name="tenantId"/>. Queries both system-wide rows
    /// (tenant_id IS NULL) and tenant-specific rows, merging them in memory with tenant-specific
    /// rows WINNING on duplicate holiday_date — so a tenant can override (or add) individual
    /// holidays on top of the system calendar without cloning the entire year.
    /// </summary>
    Task<IReadOnlyList<HolidayCalendar>> GetForCalendarAsync(
        string calendarCode,
        int year,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a row. Used by the seeder and (future) tenant-calendar endpoints. Respects the
    /// UNIQUE (tenant_id, calendar_code, holiday_date) constraint — duplicate inserts throw
    /// <c>DbUpdateException</c>.
    /// </summary>
    Task AddAsync(HolidayCalendar holiday, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-insert variant used by the seeder to push an entire country-year calendar in one
    /// round-trip. Callers are responsible for deduplication — the seeder checks existence first.
    /// </summary>
    Task AddRangeAsync(IEnumerable<HolidayCalendar> holidays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns existing (tenant_id, calendar_code, holiday_date) tuples so the seeder can skip
    /// already-inserted rows without wrapping every call in a try/catch on DbUpdateException.
    /// </summary>
    Task<IReadOnlyList<(Guid? TenantId, string CalendarCode, DateOnly HolidayDate)>>
        ListKeysForCalendarYearAsync(
            string calendarCode,
            int year,
            Guid? tenantId,
            CancellationToken cancellationToken = default);
}
