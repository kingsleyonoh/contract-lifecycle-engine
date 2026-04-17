using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IExtractionPromptRepository"/>. Always uses
/// <c>IgnoreQueryFilters</c> because <see cref="ExtractionPrompt"/> has no global query filter
/// (it doesn't implement <c>ITenantScoped</c>) — but the defensive call protects against future
/// filter additions. Explicit filtering on <c>tenant_id = @id OR tenant_id IS NULL</c> implements
/// the PRD §4.11 fallback chain.
/// </summary>
public sealed class ExtractionPromptRepository : IExtractionPromptRepository
{
    private readonly ContractDbContext _db;

    public ExtractionPromptRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<ExtractionPrompt?> GetPromptAsync(
        Guid tenantId,
        string promptType,
        CancellationToken cancellationToken = default)
    {
        // Fetch both tenant-specific and system-default in one round-trip.
        var candidates = await _db.Set<ExtractionPrompt>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.PromptType == promptType
                && p.IsActive
                && (p.TenantId == tenantId || p.TenantId == null))
            .ToListAsync(cancellationToken);

        // Tenant-specific wins over system default.
        return candidates.FirstOrDefault(p => p.TenantId == tenantId)
            ?? candidates.FirstOrDefault(p => p.TenantId == null);
    }

    public async Task AddAsync(ExtractionPrompt prompt, CancellationToken cancellationToken = default)
    {
        _db.Set<ExtractionPrompt>().Add(prompt);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionPrompt>> ListSystemDefaultsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Set<ExtractionPrompt>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == null)
            .OrderBy(p => p.PromptType)
            .ToListAsync(cancellationToken);
    }
}
