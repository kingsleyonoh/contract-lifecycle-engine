using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContractRepository"/>. Tenant scoping is enforced by the
/// global query filter on <see cref="Contract"/> — the repository never passes <c>tenant_id</c>
/// explicitly.
/// </summary>
public sealed class ContractRepository : IContractRepository
{
    private readonly ContractDbContext _db;

    public ContractRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Contract contract, CancellationToken cancellationToken = default)
    {
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Contract?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Contract contract, CancellationToken cancellationToken = default)
    {
        _db.Contracts.Update(contract);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<Contract>> ListAsync(
        ContractFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Contract> query = _db.Contracts.AsNoTracking();

        if (filters.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filters.CounterpartyId is { } cpId)
        {
            query = query.Where(c => c.CounterpartyId == cpId);
        }

        if (filters.Type is { } type)
        {
            query = query.Where(c => c.ContractType == type);
        }

        if (filters.ExpiringWithinDays is { } days && days >= 0)
        {
            // end_date <= today + N (and not null). Matches PRD §8b ?expiring_within_days= semantics.
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var cutoff = today.AddDays(days);
            query = query.Where(c => c.EndDate != null && c.EndDate <= cutoff);
        }

        // `filters.Tag` is reserved for Batch 009 (contract_tags table). Today it's accepted but
        // produces no WHERE clause so the wire contract stays stable.
        return query.ApplyCursorAsync(request, cancellationToken);
    }

    public Task<int> CountByCounterpartyAsync(Guid counterpartyId, CancellationToken cancellationToken = default)
    {
        return _db.Contracts.CountAsync(c => c.CounterpartyId == counterpartyId, cancellationToken);
    }
}
