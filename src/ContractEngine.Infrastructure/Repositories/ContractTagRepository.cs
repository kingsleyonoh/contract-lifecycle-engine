using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContractTagRepository"/>. Tenant scoping is enforced by
/// the global query filter on <see cref="ContractTag"/>. The replace flow runs inside an EF Core
/// transaction so a crash mid-way leaves the UNIQUE(tenant_id, contract_id, tag) row set intact.
/// </summary>
public sealed class ContractTagRepository : IContractTagRepository
{
    private readonly ContractDbContext _db;

    public ContractTagRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ContractTag>> ReplaceTagsAsync(
        Guid tenantId,
        Guid contractId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        // Transaction so the DELETE + INSERT batch is atomic — a failure after the DELETE must
        // roll back, never commit an empty tag set when the caller wanted replacement.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await _db.ContractTags
            .Where(t => t.ContractId == contractId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _db.ContractTags.RemoveRange(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        var rows = tags.Select(t => new ContractTag
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = contractId,
            Tag = t,
            CreatedAt = now,
        }).ToList();

        if (rows.Count > 0)
        {
            await _db.ContractTags.AddRangeAsync(rows, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        // Re-read under the tenant filter so callers receive rows with the DB-generated defaults
        // populated (CreatedAt from Postgres when the caller left it zeroed, stable ordering).
        return await _db.ContractTags
            .AsNoTracking()
            .Where(t => t.ContractId == contractId)
            .OrderBy(t => t.Tag)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContractTag>> ListByContractAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        return await _db.ContractTags
            .AsNoTracking()
            .Where(t => t.ContractId == contractId)
            .OrderBy(t => t.Tag)
            .ToListAsync(cancellationToken);
    }
}
