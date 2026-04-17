using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace ContractEngine.Core.Services;

/// <summary>
/// Stateless (apart from its injected cache) business-day calculator for PRD §5.4. Holiday sets
/// for a given <c>(calendar_code, year, tenant_id)</c> triple are loaded once from
/// <see cref="IHolidayCalendarRepository"/> and cached for 24 hours via <see cref="IMemoryCache"/>.
///
/// <para><b>Cache-key shape:</b> <c>"holidays::{code}::{year}::{tenantId?}"</c>. A null tenant id
/// uses the literal string "system" so null-tenant and "no tenant override" requests share the same
/// slot when neither has custom rows.</para>
///
/// <para><b>Sync wrapper:</b> the interface exposes synchronous methods so callers inside domain
/// services (which may or may not be async themselves) don't have to propagate a Task. We block on
/// <c>GetAwaiter().GetResult()</c> during the rare cache miss — holiday loads are tiny (~10 rows)
/// and only happen once per (code, year, tenant) combination per day. <em>Safe because this
/// service is never called from a UI sync context and never inside a transaction.</em></para>
///
/// <para><b>Lifetime model:</b> registered as a DI singleton. The repository field is evaluated
/// on every cache miss — in production the repository is resolved via
/// <see cref="IHolidayCalendarRepositoryFactory"/> so a scoped DbContext is available; tests
/// inject the repository directly via the alternate constructor.</para>
/// </summary>
public sealed class BusinessDayCalculator : IBusinessDayCalculator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly IHolidayCalendarRepositoryFactory _repositoryFactory;
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Sole public constructor — takes the factory so we can resolve a scoped repository on every
    /// cache miss without keeping a stale DbContext alive in the singleton. Tests that hold a
    /// direct <see cref="IHolidayCalendarRepository"/> should use <see cref="ForTesting"/>.
    /// </summary>
    public BusinessDayCalculator(IHolidayCalendarRepositoryFactory repositoryFactory, IMemoryCache cache)
    {
        _repositoryFactory = repositoryFactory;
        _cache = cache;
    }

    /// <summary>
    /// Factory helper for tests that hold a direct repository reference. Wraps the repo in a
    /// no-op scope and hands it to the production constructor. Not overloaded as a ctor so
    /// MS.DI ValidateOnBuild doesn't flag two-ctor ambiguity.
    /// </summary>
    public static BusinessDayCalculator ForTesting(IHolidayCalendarRepository repository, IMemoryCache cache) =>
        new(new DirectRepositoryFactory(repository), cache);

    private sealed class DirectRepositoryFactory : IHolidayCalendarRepositoryFactory
    {
        private readonly IHolidayCalendarRepository _repo;
        public DirectRepositoryFactory(IHolidayCalendarRepository repo) { _repo = repo; }
        public IHolidayCalendarRepositoryScope Create() => new DirectScope(_repo);
        private sealed class DirectScope : IHolidayCalendarRepositoryScope
        {
            public DirectScope(IHolidayCalendarRepository repo) { Repository = repo; }
            public IHolidayCalendarRepository Repository { get; }
            public void Dispose() { /* no-op — caller owns the repo */ }
        }
    }

    public bool IsBusinessDay(DateOnly date, string calendarCode, Guid? tenantId = null)
    {
        if (IsWeekend(date))
        {
            return false;
        }
        var holidays = GetHolidaySet(calendarCode, date.Year, tenantId);
        return !holidays.Contains(date);
    }

    public int BusinessDaysUntil(DateOnly target, string calendarCode, Guid? tenantId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return BusinessDaysUntilFrom(today, target, calendarCode, tenantId);
    }

    /// <summary>
    /// Testing / advanced-caller seam: compute business days between two explicit dates instead of
    /// relying on <c>DateTime.UtcNow</c>. Used by unit tests to pin a deterministic "today" and by
    /// the deadline scanner (which needs to evaluate "if today were X, would this obligation be
    /// due?" for alert windows).
    /// </summary>
    public int BusinessDaysUntilFrom(DateOnly from, DateOnly target, string calendarCode, Guid? tenantId = null)
    {
        if (from == target)
        {
            return 0;
        }

        var direction = from < target ? 1 : -1;
        var count = 0;
        var cursor = from;

        // Walk one calendar day at a time toward the target. Start-day itself is NOT counted — we
        // advance the cursor FIRST, then test. That yields the "Mon→Fri = 4" convention (Tue, Wed,
        // Thu, Fri = 4 counted; Monday is the anchor, not a business day in transit).
        while (cursor != target)
        {
            cursor = cursor.AddDays(direction);
            if (IsBusinessDay(cursor, calendarCode, tenantId))
            {
                count += direction;
            }
        }

        return count;
    }

    public DateOnly BusinessDaysAfter(DateOnly start, int businessDays, string calendarCode, Guid? tenantId = null)
    {
        // Zero-case: per the interface contract, return start unchanged if it's a business day;
        // otherwise advance forward to the next business day. This matches the "align to the next
        // workday" semantic the obligation scheduler wants.
        if (businessDays == 0)
        {
            return IsBusinessDay(start, calendarCode, tenantId)
                ? start
                : AdvanceToNextBusinessDay(start, calendarCode, tenantId, 1);
        }

        var direction = businessDays > 0 ? 1 : -1;
        var remaining = Math.Abs(businessDays);
        var cursor = start;

        while (remaining > 0)
        {
            cursor = cursor.AddDays(direction);
            if (IsBusinessDay(cursor, calendarCode, tenantId))
            {
                remaining--;
            }
        }

        return cursor;
    }

    private DateOnly AdvanceToNextBusinessDay(DateOnly start, string calendarCode, Guid? tenantId, int direction)
    {
        var cursor = start;
        while (!IsBusinessDay(cursor, calendarCode, tenantId))
        {
            cursor = cursor.AddDays(direction);
        }
        return cursor;
    }

    private static bool IsWeekend(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;

    /// <summary>
    /// Load the holiday-date set for (code, year, tenantId). Uses <see cref="IMemoryCache"/> with
    /// a 24-hour TTL so the first lookup pays for the DB round-trip and subsequent lookups are
    /// in-memory. A <see cref="HashSet{T}"/> is built once per entry for O(1) containment checks.
    /// </summary>
    private HashSet<DateOnly> GetHolidaySet(string calendarCode, int year, Guid? tenantId)
    {
        var cacheKey = BuildCacheKey(calendarCode, year, tenantId);
        if (_cache.TryGetValue(cacheKey, out HashSet<DateOnly>? cached) && cached is not null)
        {
            return cached;
        }

        using var scope = _repositoryFactory.Create();
        var rows = scope.Repository
            .GetForCalendarAsync(calendarCode, year, tenantId)
            .GetAwaiter()
            .GetResult();

        var set = new HashSet<DateOnly>(rows.Select(r => r.HolidayDate));
        _cache.Set(cacheKey, set, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            // Keep the entries out of the LRU firing line — there are ~4 countries × ~2 years
            // × (tenants that actually have overrides, usually 0) = small and hot.
            Priority = CacheItemPriority.High,
        });
        return set;
    }

    private static string BuildCacheKey(string calendarCode, int year, Guid? tenantId) =>
        $"holidays::{calendarCode}::{year}::{tenantId?.ToString() ?? "system"}";
}
