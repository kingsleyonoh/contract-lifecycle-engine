using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IExtractionJobRepository"/>. Tenant-scoped via the
/// global query filter on <see cref="ExtractionJob"/> (implements <c>ITenantScoped</c>).
/// <see cref="ListQueuedAsync"/> bypasses the filter to scan across all tenants for the
/// background processor job.
/// </summary>
public sealed class ExtractionJobRepository : IExtractionJobRepository
{
    private readonly ContractDbContext _db;

    public ExtractionJobRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<ExtractionJob?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Set<ExtractionJob>()
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task AddAsync(ExtractionJob job, CancellationToken cancellationToken = default)
    {
        _db.Set<ExtractionJob>().Add(job);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ExtractionJob job, CancellationToken cancellationToken = default)
    {
        _db.Set<ExtractionJob>().Update(job);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<ExtractionJob>> ListAsync(
        ExtractionJobFilters filters,
        PageRequest pageRequest,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Set<ExtractionJob>().AsNoTracking().AsQueryable();

        if (filters.Status.HasValue)
            query = query.Where(j => j.Status == filters.Status.Value);

        if (filters.ContractId.HasValue)
            query = query.Where(j => j.ContractId == filters.ContractId.Value);

        return await query.ApplyCursorAsync(pageRequest, cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionJob>> ListQueuedAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        return await _db.Set<ExtractionJob>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(j => j.Status == ExtractionStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
}
