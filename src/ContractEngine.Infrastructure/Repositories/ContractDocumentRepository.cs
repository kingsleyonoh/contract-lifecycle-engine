using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContractDocumentRepository"/>. Tenant scoping is
/// enforced by the global query filter on <see cref="ContractDocument"/> — this repository never
/// passes <c>tenant_id</c> explicitly. List results are paginated via the shared
/// <c>(CreatedAt, Id)</c> cursor helper.
/// </summary>
public sealed class ContractDocumentRepository : IContractDocumentRepository
{
    private readonly ContractDbContext _db;

    public ContractDocumentRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(ContractDocument document, CancellationToken cancellationToken = default)
    {
        _db.ContractDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<ContractDocument?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _db.ContractDocuments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public Task<PagedResult<ContractDocument>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ContractDocument> query = _db.ContractDocuments
            .AsNoTracking()
            .Where(d => d.ContractId == contractId);

        return query.ApplyCursorAsync(request, cancellationToken);
    }
}
