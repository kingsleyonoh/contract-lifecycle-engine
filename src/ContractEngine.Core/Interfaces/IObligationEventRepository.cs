using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>obligation_events</c> table. PRD §4.7 makes this log INSERT-only — the
/// interface intentionally exposes only add + list; there are NO <c>UpdateAsync</c> or
/// <c>DeleteAsync</c> methods. Attempts to mutate a row in place would compile-fail rather than
/// surface at runtime. Tenant scoping is enforced at the query-filter level.
/// </summary>
public interface IObligationEventRepository
{
    Task AddAsync(ObligationEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paginated event history for <paramref name="obligationId"/>, newest first via the shared
    /// <c>(CreatedAt, Id)</c> cursor helper. Events don't carry an updated_at field so the cursor
    /// is guaranteed monotonic.
    /// </summary>
    Task<PagedResult<ObligationEvent>> ListByObligationAsync(
        Guid obligationId,
        PageRequest request,
        CancellationToken cancellationToken = default);
}
