using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Jobs;

/// <summary>
/// EF Core implementation of <see cref="IStaleObligationStore"/>. Uses
/// <c>IgnoreQueryFilters()</c> for cross-tenant access since the stale-check job runs
/// without a resolved tenant context.
/// </summary>
public sealed class StaleObligationStore : IStaleObligationStore
{
    private readonly ContractDbContext _db;

    public StaleObligationStore(ContractDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Obligation>> LoadStaleObligationsAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Non-terminal, non-Pending obligations with next_due_date in the past.
        // These are the ones the deadline scanner should have transitioned.
        var terminalStatuses = new[]
        {
            ObligationStatus.Dismissed,
            ObligationStatus.Fulfilled,
            ObligationStatus.Waived,
            ObligationStatus.Expired,
            ObligationStatus.Pending,
        };

        return await _db.Obligations
            .IgnoreQueryFilters()
            .Where(o =>
                !terminalStatuses.Contains(o.Status) &&
                o.NextDueDate != null &&
                o.NextDueDate < today)
            .ToListAsync(cancellationToken);
    }
}
