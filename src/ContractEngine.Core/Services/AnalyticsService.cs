using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using FluentValidation;

namespace ContractEngine.Core.Services;

/// <summary>
/// Analytics aggregations service (PRD §8b, §10b). Composes directly against the tenant-filtered
/// <see cref="IAnalyticsQueryContext"/> — the service does not reach into EF Core types so it can
/// stay in the Core layer without taking a dependency on Infrastructure. All reads flow through
/// the global query filter (tenant isolation is automatic) and every public method requires a
/// resolved <see cref="ITenantContext"/>.
///
/// <para>Implementation notes</para>
/// <list type="bullet">
///   <item><b>Dashboard sequential awaits, not Task.WhenAll:</b> <c>DbContext</c> is NOT
///     thread-safe — firing seven reads in parallel against a single context throws
///     "second operation started on this context before a previous operation completed". A
///     future optimisation could open seven scoped contexts via <c>IServiceScopeFactory</c>, but
///     for a dashboard with 7 cheap <c>COUNT(*)</c> queries the sequential round-trip is
///     already sub-50ms locally — shipping the simpler shape first.</item>
///   <item><b>Calendar days vs. business days:</b> <c>upcoming_deadlines_{7,30}d</c> and
///     <c>expiring_contracts_90d</c> use raw calendar days today. PRD §5.4 mentions business
///     days for the Deadline Scanner; dashboards can accept the cheaper approximation. Flagged
///     as a future refinement — swap to <see cref="IBusinessDayCalculator"/> once a tenant's
///     default calendar lookup exists.</item>
/// </list>
/// </summary>
public sealed class AnalyticsService
{
    internal const int DeadlineCalendarHardCap = 1000;
    internal const int DeadlineCalendarMaxRangeDays = 365;

    private readonly IAnalyticsQueryContext _context;
    private readonly ITenantContext _tenantContext;

    public AnalyticsService(IAnalyticsQueryContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task<DashboardStats> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        RequireTenantId();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var plus7 = today.AddDays(7);
        var plus30 = today.AddDays(30);
        var plus90 = today.AddDays(90);

        // Sequential awaits — DbContext is not thread-safe. See class-level remarks.
        var activeContracts = await _context.CountContractsAsync(
            c => c.Status == ContractStatus.Active, cancellationToken);

        var pendingObligations = await _context.CountObligationsAsync(
            o => o.Status == ObligationStatus.Pending, cancellationToken);

        var overdueCount = await _context.CountObligationsAsync(
            o => o.Status == ObligationStatus.Overdue, cancellationToken);

        var upcoming7d = await _context.CountObligationsAsync(
            o => o.NextDueDate != null
                 && o.NextDueDate >= today
                 && o.NextDueDate <= plus7
                 && o.Status != ObligationStatus.Fulfilled
                 && o.Status != ObligationStatus.Waived
                 && o.Status != ObligationStatus.Dismissed
                 && o.Status != ObligationStatus.Expired,
            cancellationToken);

        var upcoming30d = await _context.CountObligationsAsync(
            o => o.NextDueDate != null
                 && o.NextDueDate >= today
                 && o.NextDueDate <= plus30
                 && o.Status != ObligationStatus.Fulfilled
                 && o.Status != ObligationStatus.Waived
                 && o.Status != ObligationStatus.Dismissed
                 && o.Status != ObligationStatus.Expired,
            cancellationToken);

        var expiringContracts90d = await _context.CountContractsAsync(
            c => c.EndDate != null
                 && c.EndDate >= today
                 && c.EndDate <= plus90
                 && (c.Status == ContractStatus.Active || c.Status == ContractStatus.Expiring),
            cancellationToken);

        var unacknowledgedAlerts = await _context.CountAlertsAsync(
            a => !a.Acknowledged, cancellationToken);

        return new DashboardStats(
            activeContracts,
            pendingObligations,
            overdueCount,
            upcoming7d,
            upcoming30d,
            expiringContracts90d,
            unacknowledgedAlerts);
    }

    /// <summary>
    /// Obligation counts grouped by <c>(type, status)</c>. <paramref name="period"/> accepts
    /// <c>"month"</c> (default — current calendar month) or <c>"year"</c>. Rows are filtered by
    /// <c>created_at</c> falling inside the window, matching the PRD intent of "activity during
    /// period".
    /// </summary>
    public async Task<ObligationsByTypeResult> GetObligationsByTypeAsync(
        string? period,
        CancellationToken cancellationToken = default)
    {
        RequireTenantId();

        var (rangeStart, rangeEndExclusive, periodLabel) = ResolvePeriod(period);

        var groups = await _context.GroupObligationsByTypeAndStatusAsync(
            rangeStart, rangeEndExclusive, cancellationToken);

        return new ObligationsByTypeResult(periodLabel, groups);
    }

    /// <summary>
    /// Total contract value grouped by <c>(status, currency)</c>. When
    /// <paramref name="counterpartyId"/> is provided, rows are further filtered to that
    /// counterparty — used for "what do we owe / what's due to us, per counterparty" views.
    /// </summary>
    public async Task<ContractValueResult> GetContractValueAsync(
        Guid? counterpartyId,
        CancellationToken cancellationToken = default)
    {
        RequireTenantId();

        var groups = await _context.GroupContractValueAsync(counterpartyId, cancellationToken);
        return new ContractValueResult(groups);
    }

    /// <summary>
    /// Obligations whose <c>next_due_date</c> (falling back to <c>deadline_date</c>) falls inside
    /// the inclusive <c>[from, to]</c> range. Hard capped at <see cref="DeadlineCalendarHardCap"/>
    /// rows; ranges > <see cref="DeadlineCalendarMaxRangeDays"/> throw a
    /// <see cref="ValidationException"/>.
    /// </summary>
    public async Task<DeadlineCalendarResult> GetDeadlineCalendarAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        RequireTenantId();

        if (to < from)
        {
            throw new ValidationException("`to` must be greater than or equal to `from`");
        }

        var span = to.DayNumber - from.DayNumber;
        if (span > DeadlineCalendarMaxRangeDays)
        {
            throw new ValidationException(
                $"Date range must be no more than {DeadlineCalendarMaxRangeDays} days; got {span}");
        }

        var items = await _context.ListDeadlineCalendarAsync(
            from, to, DeadlineCalendarHardCap, cancellationToken);
        return new DeadlineCalendarResult(items);
    }

    /// <summary>Resolves a period string → (startInclusive, endExclusive, canonicalLabel).</summary>
    internal static (DateTime Start, DateTime EndExclusive, string Label) ResolvePeriod(string? period)
    {
        var normalized = string.IsNullOrWhiteSpace(period) ? "month" : period.Trim().ToLowerInvariant();

        var now = DateTime.UtcNow;
        return normalized switch
        {
            "year" => (
                new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(now.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                now.Year.ToString("D4")),
            "month" => (
                new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
                $"{now.Year:D4}-{now.Month:D2}"),
            _ => throw new ValidationException($"period must be 'month' or 'year'; got '{period}'"),
        };
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }
}

// --- Result POCOs -----------------------------------------------------------

public sealed record DashboardStats(
    int ActiveContracts,
    int PendingObligations,
    int OverdueCount,
    int UpcomingDeadlines7d,
    int UpcomingDeadlines30d,
    int ExpiringContracts90d,
    int UnacknowledgedAlerts);

public sealed record ObligationsByTypeResult(
    string Period,
    IReadOnlyList<ObligationsByTypeGroup> Data);

public sealed record ObligationsByTypeGroup(
    ObligationType Type,
    ObligationStatus Status,
    int Count);

public sealed record ContractValueResult(IReadOnlyList<ContractValueGroup> Data);

public sealed record ContractValueGroup(
    ContractStatus Status,
    string Currency,
    decimal TotalValue,
    int ContractCount,
    Guid? CounterpartyId);

public sealed record DeadlineCalendarResult(IReadOnlyList<DeadlineCalendarItem> Data);

public sealed record DeadlineCalendarItem(
    Guid ObligationId,
    Guid ContractId,
    string Title,
    DateOnly NextDueDate,
    decimal? Amount,
    string Currency,
    ObligationStatus Status);
