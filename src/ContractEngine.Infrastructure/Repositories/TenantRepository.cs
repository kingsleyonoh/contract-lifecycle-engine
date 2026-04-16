using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITenantRepository"/>. Writes go through the standard
/// <see cref="ContractDbContext"/>; reads use <c>IgnoreQueryFilters()</c> because the tenant row
/// predates any resolved <c>ITenantContext</c> (the whole point of looking it up is to resolve
/// the context).
/// </summary>
public sealed class TenantRepository : ITenantRepository
{
    private readonly ContractDbContext _db;

    public TenantRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        return _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApiKeyHash == apiKeyHash, cancellationToken);
    }

    public Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        return _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        // The caller supplies an in-memory tenant (typically reloaded via GetByIdAsync +
        // AsNoTracking). Attach it to the change tracker in Modified state so EF Core issues
        // a UPDATE covering all scalar columns.
        _db.Tenants.Update(tenant);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
