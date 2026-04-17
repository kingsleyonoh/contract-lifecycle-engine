using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>obligations</c> table. Defined in Core so services, jobs, and seed code
/// can depend on it without a direct reference to Infrastructure / EF Core. Tenant scoping is
/// enforced at the query-filter level — callers never pass <c>tenant_id</c> explicitly.
///
/// <para>Consumed by <c>ObligationService</c> (Batch 012) and the <c>DeadlineScannerJob</c>
/// (Phase 2). The insert-only event log lives behind <see cref="IObligationEventRepository"/>.</para>
/// </summary>
public interface IObligationRepository
{
    Task AddAsync(Obligation obligation, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>null</c> for missing or cross-tenant ids (hidden by the query filter).</summary>
    Task<Obligation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Obligation obligation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists obligations for the current tenant, narrowed by the supplied <paramref name="filters"/>.
    /// Pagination uses the shared <c>(CreatedAt, Id)</c> cursor helper.
    /// </summary>
    Task<PagedResult<Obligation>> ListAsync(
        ObligationFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts obligations bound to a given contract for the current tenant. Used by
    /// ContractService when rendering obligation counts on the contract detail view (Batch 012).
    /// </summary>
    Task<int> CountByContractAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch counts obligations for a set of contracts in a single round-trip. Returns a
    /// dictionary keyed by <paramref name="contractIds"/> — contracts with zero obligations are
    /// present with value 0 (the caller never has to handle missing keys). Used by the contract
    /// list endpoint so per-page rendering doesn't fan out into N+1 count queries.
    /// Batch 026 security-audit finding I: wires the real count into list responses.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> CountByContractIdsAsync(
        IReadOnlyCollection<Guid> contractIds,
        CancellationToken cancellationToken = default);
}
