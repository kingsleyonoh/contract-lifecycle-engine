using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Jobs;

/// <summary>
/// EF Core implementation of <see cref="IAutoRenewalStore"/>. Uses
/// <c>IgnoreQueryFilters()</c> for cross-tenant access since the auto-renewal job runs
/// without a resolved tenant context.
/// </summary>
public sealed class AutoRenewalStore : IAutoRenewalStore
{
    private readonly ContractDbContext _db;

    public AutoRenewalStore(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Contract>> LoadAutoRenewalCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return await _db.Contracts
            .IgnoreQueryFilters()
            .Where(c =>
                c.Status == ContractStatus.Expiring &&
                c.AutoRenewal &&
                c.EndDate != null &&
                c.EndDate <= today)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveRenewalAsync(
        Contract contract,
        ContractVersion version,
        CancellationToken cancellationToken = default)
    {
        _db.Contracts.Update(contract);
        _db.ContractVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
