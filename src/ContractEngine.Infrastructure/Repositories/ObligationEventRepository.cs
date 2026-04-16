using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Infrastructure.Data;
using ContractEngine.Infrastructure.Pagination;
using Microsoft.EntityFrameworkCore;

namespace ContractEngine.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IObligationEventRepository"/>. PRD §4.7 makes this log
/// INSERT-only: the interface exposes no <c>UpdateAsync</c> / <c>DeleteAsync</c>, and this class
/// implements exactly two methods to match. Tenant scoping is enforced by the global query filter
/// on <see cref="ObligationEvent"/>.
/// </summary>
public sealed class ObligationEventRepository : IObligationEventRepository
{
    private readonly ContractDbContext _db;

    public ObligationEventRepository(ContractDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(ObligationEvent @event, CancellationToken cancellationToken = default)
    {
        _db.ObligationEvents.Add(@event);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<PagedResult<ObligationEvent>> ListByObligationAsync(
        Guid obligationId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ObligationEvent> query = _db.ObligationEvents
            .AsNoTracking()
            .Where(e => e.ObligationId == obligationId);

        return query.ApplyCursorAsync(request, cancellationToken);
    }
}
