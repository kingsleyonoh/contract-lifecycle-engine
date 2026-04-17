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
/// EF Core implementation of <see cref="IObligationRepository"/>. Tenant scoping is enforced by
/// the global query filter on <see cref="Obligation"/> — the repository never passes
/// <c>tenant_id</c> explicitly. Cursor pagination via the shared
/// <see cref="CursorPaginationExtensions.ApplyCursorAsync"/>.
/// </summary>
public sealed class ObligationRepository : IObligationRepository
{
    private readonly ContractDbContext _db;

    public ObligationRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Obligation obligation, CancellationToken cancellationToken = default)
    {
        _db.Obligations.Add(obligation);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Obligation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _db.Obligations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Obligation obligation, CancellationToken cancellationToken = default)
    {
        // Detach any existing tracked entity with the same key before reattaching the caller's
        // instance. The archive cascade (Batch 013) pulls obligations via an AsNoTracking list,
        // but earlier calls in the same DbContext scope (e.g. a prior Confirm on the same row)
        // may have left the parent instance tracked — EF will throw on duplicate-key attach.
        var tracked = _db.ChangeTracker.Entries<Obligation>()
            .FirstOrDefault(e => e.Entity.Id == obligation.Id && !ReferenceEquals(e.Entity, obligation));
        if (tracked is not null)
        {
            tracked.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }

        _db.Obligations.Update(obligation);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<Obligation>> ListAsync(
        ObligationFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Obligation> query = _db.Obligations.AsNoTracking();

        if (filters.Status is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (filters.Type is { } type)
        {
            query = query.Where(o => o.ObligationType == type);
        }

        if (filters.ContractId is { } contractId)
        {
            query = query.Where(o => o.ContractId == contractId);
        }

        if (filters.DueBefore is { } before)
        {
            query = query.Where(o => o.NextDueDate != null && o.NextDueDate <= before);
        }

        if (filters.DueAfter is { } after)
        {
            query = query.Where(o => o.NextDueDate != null && o.NextDueDate >= after);
        }

        if (!string.IsNullOrWhiteSpace(filters.ResponsibleParty))
        {
            // Wire format is snake_case; parse into the enum so the EF query stays index-friendly
            // (converter maps back to the string column under the hood).
            if (TryParseResponsibleParty(filters.ResponsibleParty, out var parsed))
            {
                query = query.Where(o => o.ResponsibleParty == parsed);
            }
            else
            {
                // Invalid value → empty result set rather than throwing. Endpoint-layer
                // FluentValidation (Batch 012) will reject malformed input up-front so this branch
                // is defensive.
                query = query.Where(o => false);
            }
        }

        return query.ApplyCursorAsync(request, cancellationToken);
    }

    public Task<int> CountByContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        return _db.Obligations.CountAsync(o => o.ContractId == contractId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByContractIdsAsync(
        IReadOnlyCollection<Guid> contractIds,
        CancellationToken cancellationToken = default)
    {
        if (contractIds is null || contractIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        // GROUP BY + COUNT in one round-trip. The global query filter on Obligation already
        // enforces tenant isolation, so the caller's tenant is implicit.
        var grouped = await _db.Obligations
            .Where(o => contractIds.Contains(o.ContractId))
            .GroupBy(o => o.ContractId)
            .Select(g => new { ContractId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // Seed every requested id with 0 so callers never have to null-check. Contracts that had
        // no obligations simply don't appear in the grouped result — we write them in afterwards.
        var result = new Dictionary<Guid, int>(contractIds.Count);
        foreach (var id in contractIds)
        {
            result[id] = 0;
        }
        foreach (var entry in grouped)
        {
            result[entry.ContractId] = entry.Count;
        }
        return result;
    }

    private static bool TryParseResponsibleParty(string raw, out ResponsibleParty parsed)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "us":
                parsed = ResponsibleParty.Us;
                return true;
            case "counterparty":
                parsed = ResponsibleParty.Counterparty;
                return true;
            case "both":
                parsed = ResponsibleParty.Both;
                return true;
            default:
                parsed = default;
                return false;
        }
    }
}
