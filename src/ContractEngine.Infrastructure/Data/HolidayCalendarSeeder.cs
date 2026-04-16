using ContractEngine.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Data;

/// <summary>
/// Idempotent seeder for the <c>holiday_calendars</c> table. Inserts SYSTEM-WIDE rows
/// (<c>tenant_id = null</c>) for the four portfolio calendars (US / DE / UK / NL) for the current
/// year and the next year. Safe to call on every application start — the seeder checks existence
/// by <c>(tenant_id IS NULL, calendar_code, holiday_date)</c> before inserting.
///
/// <para><b>Date accuracy:</b> dates are hardcoded from published calendars. Easter-dependent
/// holidays (Good Friday, Easter Monday, Ascension, Pentecost Monday) are entered directly rather
/// than computed — less code, no risk of off-by-one in Computus implementations, easy to audit.
/// 2027 Easter is 28 March 2027; all Easter-tied dates flow from there.</para>
/// </summary>
public static class HolidayCalendarSeeder
{
    public static async Task SeedAsync(ContractDbContext db, CancellationToken cancellationToken = default)
    {
        var allRows = BuildSystemWideRows();
        var grouped = allRows.GroupBy(r => (r.CalendarCode, r.Year));

        foreach (var group in grouped)
        {
            var (code, year) = group.Key;

            // Fetch existing dates for this (tenant_id IS NULL, code, year) triple in one round-trip.
            var existingDates = await db.HolidayCalendars
                .IgnoreQueryFilters()
                .Where(h => h.TenantId == null && h.CalendarCode == code && h.Year == year)
                .Select(h => h.HolidayDate)
                .ToListAsync(cancellationToken);
            var existingSet = new HashSet<DateOnly>(existingDates);

            var toInsert = group.Where(r => !existingSet.Contains(r.HolidayDate)).ToList();
            if (toInsert.Count == 0)
            {
                continue;
            }

            await db.HolidayCalendars.AddRangeAsync(toInsert, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<HolidayCalendar> BuildSystemWideRows()
    {
        var rows = new List<HolidayCalendar>();
        rows.AddRange(US2026());
        rows.AddRange(US2027());
        rows.AddRange(DE2026());
        rows.AddRange(DE2027());
        rows.AddRange(UK2026());
        rows.AddRange(UK2027());
        rows.AddRange(NL2026());
        rows.AddRange(NL2027());
        return rows;
    }

    private static IEnumerable<HolidayCalendar> US2026() => new[]
    {
        Row("US", 2026, new(2026, 1, 1), "New Year's Day"),
        Row("US", 2026, new(2026, 1, 19), "Martin Luther King Jr. Day"),
        Row("US", 2026, new(2026, 2, 16), "Presidents' Day"),
        Row("US", 2026, new(2026, 5, 25), "Memorial Day"),
        Row("US", 2026, new(2026, 6, 19), "Juneteenth"),
        Row("US", 2026, new(2026, 7, 3), "Independence Day (observed)"),
        Row("US", 2026, new(2026, 9, 7), "Labor Day"),
        Row("US", 2026, new(2026, 10, 12), "Columbus Day"),
        Row("US", 2026, new(2026, 11, 11), "Veterans Day"),
        Row("US", 2026, new(2026, 11, 26), "Thanksgiving Day"),
        Row("US", 2026, new(2026, 12, 25), "Christmas Day"),
    };

    private static IEnumerable<HolidayCalendar> US2027() => new[]
    {
        Row("US", 2027, new(2027, 1, 1), "New Year's Day"),
        Row("US", 2027, new(2027, 1, 18), "Martin Luther King Jr. Day"),
        Row("US", 2027, new(2027, 2, 15), "Presidents' Day"),
        Row("US", 2027, new(2027, 5, 31), "Memorial Day"),
        Row("US", 2027, new(2027, 6, 18), "Juneteenth (observed)"),
        Row("US", 2027, new(2027, 7, 5), "Independence Day (observed)"),
        Row("US", 2027, new(2027, 9, 6), "Labor Day"),
        Row("US", 2027, new(2027, 10, 11), "Columbus Day"),
        Row("US", 2027, new(2027, 11, 11), "Veterans Day"),
        Row("US", 2027, new(2027, 11, 25), "Thanksgiving Day"),
        Row("US", 2027, new(2027, 12, 24), "Christmas Day (observed)"),
    };

    private static IEnumerable<HolidayCalendar> DE2026() => new[]
    {
        Row("DE", 2026, new(2026, 1, 1), "Neujahr"),
        Row("DE", 2026, new(2026, 4, 3), "Karfreitag"),
        Row("DE", 2026, new(2026, 4, 6), "Ostermontag"),
        Row("DE", 2026, new(2026, 5, 1), "Tag der Arbeit"),
        Row("DE", 2026, new(2026, 5, 14), "Christi Himmelfahrt"),
        Row("DE", 2026, new(2026, 5, 25), "Pfingstmontag"),
        Row("DE", 2026, new(2026, 10, 3), "Tag der Deutschen Einheit"),
        Row("DE", 2026, new(2026, 12, 25), "1. Weihnachtstag"),
        Row("DE", 2026, new(2026, 12, 26), "2. Weihnachtstag"),
    };

    private static IEnumerable<HolidayCalendar> DE2027() => new[]
    {
        // Easter 2027-03-28 → Good Friday 03-26, Easter Monday 03-29, Ascension 05-06, Pentecost Mon 05-17.
        Row("DE", 2027, new(2027, 1, 1), "Neujahr"),
        Row("DE", 2027, new(2027, 3, 26), "Karfreitag"),
        Row("DE", 2027, new(2027, 3, 29), "Ostermontag"),
        Row("DE", 2027, new(2027, 5, 1), "Tag der Arbeit"),
        Row("DE", 2027, new(2027, 5, 6), "Christi Himmelfahrt"),
        Row("DE", 2027, new(2027, 5, 17), "Pfingstmontag"),
        Row("DE", 2027, new(2027, 10, 3), "Tag der Deutschen Einheit"),
        Row("DE", 2027, new(2027, 12, 25), "1. Weihnachtstag"),
        Row("DE", 2027, new(2027, 12, 26), "2. Weihnachtstag"),
    };

    private static IEnumerable<HolidayCalendar> UK2026() => new[]
    {
        Row("UK", 2026, new(2026, 1, 1), "New Year's Day"),
        Row("UK", 2026, new(2026, 4, 3), "Good Friday"),
        Row("UK", 2026, new(2026, 4, 6), "Easter Monday"),
        Row("UK", 2026, new(2026, 5, 4), "Early May Bank Holiday"),
        Row("UK", 2026, new(2026, 5, 25), "Spring Bank Holiday"),
        Row("UK", 2026, new(2026, 8, 31), "Summer Bank Holiday"),
        Row("UK", 2026, new(2026, 12, 25), "Christmas Day"),
        Row("UK", 2026, new(2026, 12, 28), "Boxing Day (substitute)"),
    };

    private static IEnumerable<HolidayCalendar> UK2027() => new[]
    {
        Row("UK", 2027, new(2027, 1, 1), "New Year's Day"),
        Row("UK", 2027, new(2027, 3, 26), "Good Friday"),
        Row("UK", 2027, new(2027, 3, 29), "Easter Monday"),
        Row("UK", 2027, new(2027, 5, 3), "Early May Bank Holiday"),
        Row("UK", 2027, new(2027, 5, 31), "Spring Bank Holiday"),
        Row("UK", 2027, new(2027, 8, 30), "Summer Bank Holiday"),
        Row("UK", 2027, new(2027, 12, 27), "Christmas Day (substitute)"),
        Row("UK", 2027, new(2027, 12, 28), "Boxing Day (substitute)"),
    };

    private static IEnumerable<HolidayCalendar> NL2026() => new[]
    {
        Row("NL", 2026, new(2026, 1, 1), "Nieuwjaarsdag"),
        Row("NL", 2026, new(2026, 4, 3), "Goede Vrijdag"),
        Row("NL", 2026, new(2026, 4, 6), "Tweede Paasdag"),
        Row("NL", 2026, new(2026, 4, 27), "Koningsdag"),
        Row("NL", 2026, new(2026, 5, 5), "Bevrijdingsdag"),
        Row("NL", 2026, new(2026, 5, 14), "Hemelvaartsdag"),
        Row("NL", 2026, new(2026, 5, 25), "Tweede Pinksterdag"),
        Row("NL", 2026, new(2026, 12, 25), "Eerste Kerstdag"),
        Row("NL", 2026, new(2026, 12, 26), "Tweede Kerstdag"),
    };

    private static IEnumerable<HolidayCalendar> NL2027() => new[]
    {
        Row("NL", 2027, new(2027, 1, 1), "Nieuwjaarsdag"),
        Row("NL", 2027, new(2027, 3, 26), "Goede Vrijdag"),
        Row("NL", 2027, new(2027, 3, 29), "Tweede Paasdag"),
        Row("NL", 2027, new(2027, 4, 27), "Koningsdag"),
        Row("NL", 2027, new(2027, 5, 5), "Bevrijdingsdag"),
        Row("NL", 2027, new(2027, 5, 6), "Hemelvaartsdag"),
        Row("NL", 2027, new(2027, 5, 17), "Tweede Pinksterdag"),
        Row("NL", 2027, new(2027, 12, 25), "Eerste Kerstdag"),
        Row("NL", 2027, new(2027, 12, 26), "Tweede Kerstdag"),
    };

    private static HolidayCalendar Row(string code, int year, DateOnly date, string name) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = null,
        CalendarCode = code,
        Year = year,
        HolidayDate = date,
        HolidayName = name,
    };
}
