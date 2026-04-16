namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Business-day arithmetic respecting weekends and holiday calendars (PRD §5.4). Implementations
/// are stateless — registered as a DI singleton. Holiday lookups go through
/// <see cref="IHolidayCalendarRepository"/> with an in-memory cache keyed on (calendar_code, year)
/// to avoid a DB round-trip on every obligation deadline calculation.
///
/// <para><b>Tenant-specific overrides:</b> all three methods accept an optional
/// <c>tenantId</c>. When supplied, tenant-specific holidays are merged on top of the system-wide
/// rows (tenant wins on duplicate date). When null, only system-wide rows are used.</para>
/// </summary>
public interface IBusinessDayCalculator
{
    /// <summary>
    /// Business days between today (caller-local, UTC by default) and <paramref name="target"/>.
    /// <list type="bullet">
    ///   <item>Today itself is NOT counted — a Monday-to-Tuesday call returns 1, not 2.</item>
    ///   <item>Weekends and holidays are skipped.</item>
    ///   <item>Returns a negative number if <paramref name="target"/> is in the past.</item>
    ///   <item>Returns 0 if <paramref name="target"/> is today.</item>
    /// </list>
    /// </summary>
    int BusinessDaysUntil(DateOnly target, string calendarCode, Guid? tenantId = null);

    /// <summary>
    /// Adds <paramref name="businessDays"/> business days to <paramref name="start"/>, skipping
    /// weekends and holidays.
    /// <list type="bullet">
    ///   <item><c>businessDays = 0</c>: returns <paramref name="start"/> unchanged if it is a
    ///     business day; otherwise advances forward to the next business day.</item>
    ///   <item>Negative values walk backwards by the absolute number of business days.</item>
    /// </list>
    /// </summary>
    DateOnly BusinessDaysAfter(DateOnly start, int businessDays, string calendarCode, Guid? tenantId = null);

    /// <summary>
    /// <c>true</c> when <paramref name="date"/> is Monday–Friday AND not a holiday in the supplied
    /// calendar (merged with the tenant override set when <paramref name="tenantId"/> is non-null).
    /// </summary>
    bool IsBusinessDay(DateOnly date, string calendarCode, Guid? tenantId = null);
}
