using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IHolidayCalendarRepository"/>. Always calls
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{T}"/> defensively — even though
/// <see cref="HolidayCalendar"/> isn't <c>ITenantScoped</c> today, a future global filter on every
/// DbSet (e.g. for soft-delete) must not hide system-wide rows from this repository. Explicit
/// filtering on <c>tenant_id = @id OR tenant_id IS NULL</c> keeps the contract clear.
/// </summary>
public sealed class HolidayCalendarRepository : IHolidayCalendarRepository
{
    private readonly ContractDbContext _db;

    public HolidayCalendarRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<HolidayCalendar>> GetForCalendarAsync(
        string calendarCode,
        int year,
        Guid? tenantId,
        CancellationToken cancellationToken = default)
    {
        // Pull both tenant-specific and system-wide rows in one round-trip. Merge is done in
        // memory so tenant-specific rows can shadow system-wide rows for the same holiday_date
        // (PRD §4.10 — "tenant custom holidays override the system calendar").
        var rows = await _db.HolidayCalendars
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.CalendarCode == calendarCode
                && h.Year == year
                && (h.TenantId == tenantId || h.TenantId == null))
            .ToListAsync(cancellationToken);

        if (tenantId is null)
        {
            // No tenant context — return system-wide set as-is (rows with tenant_id != null are
            // already excluded by the WHERE clause since tenantId is null).
            return rows;
        }

        // Tenant context present: tenant-specific row wins on duplicate (code, date). Build a
        // dictionary keyed on HolidayDate, preferring the tenant entry when both exist.
        var merged = new Dictionary<DateOnly, HolidayCalendar>(rows.Count);
        foreach (var row in rows)
        {
            // Skip system-wide entry if tenant entry for same date is already present.
            if (merged.TryGetValue(row.HolidayDate, out var existing))
            {
                if (existing.TenantId is not null)
                {
                    // Already have tenant-specific; keep it.
                    continue;
                }
                // Existing is system-wide; incoming may be tenant-specific — let it overwrite.
            }
            merged[row.HolidayDate] = row;
        }
        return merged.Values.ToList();
    }

    public async Task AddAsync(HolidayCalendar holiday, CancellationToken cancellationToken = default)
    {
        _db.HolidayCalendars.Add(holiday);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRangeAsync(IEnumerable<HolidayCalendar> holidays, CancellationToken cancellationToken = default)
    {
        await _db.HolidayCalendars.AddRangeAsync(holidays, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(Guid? TenantId, string CalendarCode, DateOnly HolidayDate)>>
        ListKeysForCalendarYearAsync(
            string calendarCode,
            int year,
            Guid? tenantId,
            CancellationToken cancellationToken = default)
    {
        var rows = await _db.HolidayCalendars
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.CalendarCode == calendarCode
                && h.Year == year
                && h.TenantId == tenantId)
            .Select(h => new { h.TenantId, h.CalendarCode, h.HolidayDate })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.TenantId, r.CalendarCode, r.HolidayDate)).ToList();
    }
}
