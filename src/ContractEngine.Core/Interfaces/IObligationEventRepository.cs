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

    /// <summary>
    /// Returns the full ordered-ascending-by-<c>created_at</c> event history for a single obligation.
    /// Used by <c>ObligationService.GetByIdWithEventsAsync</c> — the obligation detail payload
    /// inlines the event timeline so a typical UI request is one REST round-trip. Tenant scoping
    /// still applies via the global query filter. Unlike <see cref="ListByObligationAsync"/> this
    /// skips pagination because the event history for a single obligation is bounded by the number
    /// of state transitions (rare &gt; 20 even on long-lived rows).
    /// </summary>
    Task<IReadOnlyList<ObligationEvent>> ListAllByObligationAscAsync(
        Guid obligationId,
        CancellationToken cancellationToken = default);
}
