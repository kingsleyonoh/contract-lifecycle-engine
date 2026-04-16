using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>contract_versions</c> table. Defined in Core so services can depend on
/// it without referencing EF Core. Tenant scoping is enforced at the query-filter level — callers
/// never pass <c>tenant_id</c> explicitly.
/// </summary>
public interface IContractVersionRepository
{
    Task AddAsync(ContractVersion version, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>null</c> for missing or cross-tenant ids (hidden by the query filter).</summary>
    Task<ContractVersion?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paginated version history for <paramref name="contractId"/>, newest first via the shared
    /// <c>(CreatedAt, Id)</c> cursor helper.
    /// </summary>
    Task<PagedResult<ContractVersion>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the next monotonically-increasing version number for the given contract. Returns
    /// <c>1</c> when no rows exist yet (first POST to the versions endpoint).
    /// </summary>
    Task<int> GetNextVersionNumberAsync(Guid contractId, CancellationToken cancellationToken = default);
}
