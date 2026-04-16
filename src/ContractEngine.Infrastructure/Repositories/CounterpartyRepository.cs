using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICounterpartyRepository"/>. All reads flow through the
/// standard <see cref="ContractDbContext"/>, so the tenant query filter silently scopes each
/// query to the resolved tenant. Writes are tagged with a <c>TenantId</c> by the service layer
/// before they reach this repository.
/// </summary>
public sealed class CounterpartyRepository : ICounterpartyRepository
{
    private readonly ContractDbContext _db;

    public CounterpartyRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Counterparty counterparty, CancellationToken cancellationToken = default)
    {
        _db.Counterparties.Add(counterparty);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Counterparty?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // The global tenant query filter hides rows that belong to other tenants, so a foreign id
        // returns null without any explicit tenant comparison here.
        return _db.Counterparties
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Counterparty counterparty, CancellationToken cancellationToken = default)
    {
        _db.Counterparties.Update(counterparty);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<Counterparty>> ListAsync(
        string? searchTerm,
        string? industry,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Counterparty> query = _db.Counterparties.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            // EF.Functions.ILike compiles to Postgres ILIKE for case-insensitive matching.
            var pattern = $"%{searchTerm}%";
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern));
        }

        if (!string.IsNullOrWhiteSpace(industry))
        {
            query = query.Where(c => c.Industry == industry);
        }

        return query.ApplyCursorAsync(request, cancellationToken);
    }

    public Task<int> GetContractCountAsync(Guid counterpartyId, CancellationToken cancellationToken = default)
    {
        // Real count. Global tenant query filter on the Contract entity scopes this to the
        // current tenant automatically — a foreign counterparty id returns 0 without any explicit
        // tenant comparison here.
        return _db.Contracts.CountAsync(c => c.CounterpartyId == counterpartyId, cancellationToken);
    }
}
