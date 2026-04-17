using System.Linq.Expressions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Analytics;

/// <summary>
/// EF Core-backed implementation of <see cref="IAnalyticsQueryContext"/>. All reads flow through
/// the tenant-filtered <see cref="ContractDbContext"/> so isolation is free.
///
/// <para>The grouping queries push as much work into Postgres as possible (GROUP BY + aggregate),
/// then project to Core DTOs in a second pass. The deadline calendar query falls back to
/// <c>deadline_date</c> when <c>next_due_date</c> is null so one-shot obligations with no
/// computed "next due" still appear.</para>
/// </summary>
public sealed class EfAnalyticsQueryContext : IAnalyticsQueryContext
{
    private readonly ContractDbContext _db;

    public EfAnalyticsQueryContext(ContractDbContext db)
    {
        _db = db;
    }

    public Task<int> CountContractsAsync(
        Expression<Func<Contract, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _db.Contracts.AsNoTracking().CountAsync(predicate, cancellationToken);

    public Task<int> CountObligationsAsync(
        Expression<Func<Obligation, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _db.Obligations.AsNoTracking().CountAsync(predicate, cancellationToken);

    public Task<int> CountAlertsAsync(
        Expression<Func<DeadlineAlert, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        _db.DeadlineAlerts.AsNoTracking().CountAsync(predicate, cancellationToken);

    public async Task<IReadOnlyList<ObligationsByTypeGroup>> GroupObligationsByTypeAndStatusAsync(
        DateTime createdAtStart,
        DateTime createdAtEndExclusive,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Obligations
            .AsNoTracking()
            .Where(o => o.CreatedAt >= createdAtStart && o.CreatedAt < createdAtEndExclusive)
            .GroupBy(o => new { o.ObligationType, o.Status })
            .Select(g => new
            {
                g.Key.ObligationType,
                g.Key.Status,
                Count = g.Count(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new ObligationsByTypeGroup(r.ObligationType, r.Status, r.Count))
            .ToList();
    }

    public async Task<IReadOnlyList<ContractValueGroup>> GroupContractValueAsync(
        Guid? counterpartyId,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Contracts.AsNoTracking().Where(c => c.TotalValue != null);
        if (counterpartyId is { } cp)
        {
            query = query.Where(c => c.CounterpartyId == cp);
        }

        // Grouping by (Status, Currency, CounterpartyId?) keeps the math safe when multiple
        // currencies live under the same tenant. When a counterparty filter is applied, we also
        // carry the counterparty id through so callers can display a per-party breakdown.
        if (counterpartyId is null)
        {
            var rows = await query
                .GroupBy(c => new { c.Status, c.Currency })
                .Select(g => new
                {
                    g.Key.Status,
                    g.Key.Currency,
                    TotalValue = g.Sum(c => c.TotalValue ?? 0m),
                    ContractCount = g.Count(),
                })
                .ToListAsync(cancellationToken);

            return rows
                .Select(r => new ContractValueGroup(r.Status, r.Currency, r.TotalValue, r.ContractCount, null))
                .ToList();
        }
        else
        {
            var rows = await query
                .GroupBy(c => new { c.Status, c.Currency, c.CounterpartyId })
                .Select(g => new
                {
                    g.Key.Status,
                    g.Key.Currency,
                    g.Key.CounterpartyId,
                    TotalValue = g.Sum(c => c.TotalValue ?? 0m),
                    ContractCount = g.Count(),
                })
                .ToListAsync(cancellationToken);

            return rows
                .Select(r => new ContractValueGroup(r.Status, r.Currency, r.TotalValue, r.ContractCount, r.CounterpartyId))
                .ToList();
        }
    }

    public async Task<IReadOnlyList<DeadlineCalendarItem>> ListDeadlineCalendarAsync(
        DateOnly from,
        DateOnly to,
        int hardCap,
        CancellationToken cancellationToken = default)
    {
        // Only include obligations still considered "active-ish". Fulfilled / waived / dismissed /
        // expired rows shouldn't clutter the calendar.
        var rows = await _db.Obligations
            .AsNoTracking()
            .Where(o => o.NextDueDate != null
                        && o.NextDueDate >= from
                        && o.NextDueDate <= to
                        && o.Status != ObligationStatus.Fulfilled
                        && o.Status != ObligationStatus.Waived
                        && o.Status != ObligationStatus.Dismissed
                        && o.Status != ObligationStatus.Expired)
            .OrderBy(o => o.NextDueDate)
            .ThenBy(o => o.Id)
            .Take(hardCap)
            .Select(o => new
            {
                o.Id,
                o.ContractId,
                o.Title,
                o.NextDueDate,
                o.Amount,
                o.Currency,
                o.Status,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new DeadlineCalendarItem(
                r.Id,
                r.ContractId,
                r.Title,
                r.NextDueDate!.Value,
                r.Amount,
                r.Currency,
                r.Status))
            .ToList();
    }
}
