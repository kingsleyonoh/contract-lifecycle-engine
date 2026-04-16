using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContractVersionRepository"/>. Tenant scoping is enforced
/// by the global query filter on <see cref="ContractVersion"/>. List results page via the shared
/// <c>(CreatedAt, Id)</c> cursor helper (newest first).
/// </summary>
public sealed class ContractVersionRepository : IContractVersionRepository
{
    private readonly ContractDbContext _db;

    public ContractVersionRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(ContractVersion version, CancellationToken cancellationToken = default)
    {
        _db.ContractVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<ContractVersion?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _db.ContractVersions.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
    }

    public Task<PagedResult<ContractVersion>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ContractVersion> query = _db.ContractVersions
            .AsNoTracking()
            .Where(v => v.ContractId == contractId);

        return query.ApplyCursorAsync(request, cancellationToken);
    }

    public async Task<int> GetNextVersionNumberAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        // MaxAsync over an empty set throws — use a nullable projection so an empty set yields 0.
        var highest = await _db.ContractVersions
            .Where(v => v.ContractId == contractId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken);
        return (highest ?? 0) + 1;
    }
}
