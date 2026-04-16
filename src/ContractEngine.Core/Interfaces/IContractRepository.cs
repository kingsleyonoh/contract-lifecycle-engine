using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>contracts</c> table. Defined in Core so
/// <see cref="Services.ContractService"/> and future job/seed code can depend on it without a
/// direct reference to Infrastructure / EF Core. Tenant scoping is enforced at the query-filter
/// level — callers never pass <c>tenant_id</c> explicitly.
/// </summary>
public interface IContractRepository
{
    Task AddAsync(Contract contract, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>null</c> for missing or cross-tenant ids (hidden by the query filter).</summary>
    Task<Contract?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Contract contract, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists contracts for the current tenant, narrowed by the supplied <paramref name="filters"/>.
    /// Pagination uses the shared <c>(CreatedAt, Id)</c> cursor helper.
    /// </summary>
    Task<PagedResult<Contract>> ListAsync(
        ContractFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number of contracts associated with the given counterparty for the current
    /// tenant. Used by <see cref="Services.CounterpartyService.GetContractCountAsync"/> once this
    /// repository ships.
    /// </summary>
    Task<int> CountByCounterpartyAsync(Guid counterpartyId, CancellationToken cancellationToken = default);
}
