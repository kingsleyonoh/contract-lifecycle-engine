using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Cross-tenant read surface for the <c>StaleObligationCheckerCore</c>. Lives in Core so the
/// stale-check logic can be unit-tested without a DbContext. The real implementation in
/// Infrastructure uses <c>IgnoreQueryFilters()</c> to bypass the tenant query filter (the job
/// runs without a resolved tenant).
/// </summary>
public interface IStaleObligationStore
{
    /// <summary>
    /// Returns non-terminal obligations across all tenants where <c>next_due_date</c> is in
    /// the past and the obligation is in a non-terminal, non-Pending state (Active, Upcoming,
    /// Due — statuses that the deadline scanner should have transitioned).
    /// </summary>
    Task<IReadOnlyList<Obligation>> LoadStaleObligationsAsync(
        CancellationToken cancellationToken = default);
}
