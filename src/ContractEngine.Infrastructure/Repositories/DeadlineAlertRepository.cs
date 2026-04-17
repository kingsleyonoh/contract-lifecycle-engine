using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IDeadlineAlertRepository"/>. Tenant scoping is enforced by
/// the global query filter on <see cref="DeadlineAlert"/> — the repository never passes
/// <c>tenant_id</c> explicitly on reads. Bulk acknowledge uses EF Core's
/// <c>ExecuteUpdateAsync</c> so a single <c>UPDATE</c> round-trip updates every matching row.
///
/// <para><b>BulkAcknowledge note:</b> <c>ExecuteUpdateAsync</c> honours EF query filters on
/// EF Core 8+, so the global tenant filter on <see cref="DeadlineAlert"/> is applied
/// transparently. We still pass the resolved <paramref>tenantId</paramref> into the query as an
/// additional defensive filter — cheap, and explicit code makes the SQL review trivial.</para>
/// </summary>
public sealed class DeadlineAlertRepository : IDeadlineAlertRepository
{
    private readonly ContractDbContext _db;

    public DeadlineAlertRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(DeadlineAlert alert, CancellationToken cancellationToken = default)
    {
        _db.DeadlineAlerts.Add(alert);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<DeadlineAlert?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _db.DeadlineAlerts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public Task<DeadlineAlert?> FindByKeyAsync(
        Guid obligationId,
        AlertType alertType,
        int? daysRemaining,
        CancellationToken cancellationToken = default)
    {
        // The idempotency key includes days_remaining, which is nullable — so we need to match
        // NULL explicitly (Equals on int? does the right thing inside EF, but using a conditional
        // keeps the generated SQL readable).
        return _db.DeadlineAlerts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.ObligationId == obligationId
                    && a.AlertType == alertType
                    && a.DaysRemaining == daysRemaining,
                cancellationToken);
    }

    public async Task UpdateAsync(DeadlineAlert alert, CancellationToken cancellationToken = default)
    {
        // Mirror the ObligationRepository pattern: detach any prior tracked instance sharing the
        // same key before reattaching the caller's copy. A list operation in the same scope
        // returns AsNoTracking rows — but callers may reuse tracked entities across requests.
        var tracked = _db.ChangeTracker.Entries<DeadlineAlert>()
            .FirstOrDefault(e => e.Entity.Id == alert.Id && !ReferenceEquals(e.Entity, alert));
        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        _db.DeadlineAlerts.Update(alert);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<DeadlineAlert>> ListAsync(
        AlertFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<DeadlineAlert> query = _db.DeadlineAlerts.AsNoTracking();

        if (filters.Acknowledged is { } ack)
        {
            query = query.Where(a => a.Acknowledged == ack);
        }

        if (filters.AlertType is { } type)
        {
            query = query.Where(a => a.AlertType == type);
        }

        if (filters.ContractId is { } contractId)
        {
            query = query.Where(a => a.ContractId == contractId);
        }

        return query.ApplyCursorAsync(request, cancellationToken);
    }

    public async Task<int> BulkAcknowledgeAsync(
        Guid tenantId,
        string acknowledgedBy,
        Guid? contractId,
        AlertType? alertType,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Defensive tenant filter — the global query filter also applies, but repeating it here
        // keeps the generated UPDATE self-contained and easy to audit in logs.
        IQueryable<DeadlineAlert> query = _db.DeadlineAlerts
            .Where(a => a.TenantId == tenantId && !a.Acknowledged);

        if (contractId is { } cid)
        {
            query = query.Where(a => a.ContractId == cid);
        }

        if (alertType is { } type)
        {
            query = query.Where(a => a.AlertType == type);
        }

        return await query.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Acknowledged, true)
            .SetProperty(a => a.AcknowledgedAt, now)
            .SetProperty(a => a.AcknowledgedBy, acknowledgedBy),
            cancellationToken);
    }
}
