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
