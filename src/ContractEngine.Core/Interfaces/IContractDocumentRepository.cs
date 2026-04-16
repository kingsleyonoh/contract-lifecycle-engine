using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>contract_documents</c> table. Core-layer interface so the service can
/// depend on it without referencing EF Core. Tenant scoping is enforced at the query-filter
/// level — callers never pass <c>tenant_id</c> explicitly.
/// </summary>
public interface IContractDocumentRepository
{
    Task AddAsync(ContractDocument document, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>null</c> for missing or cross-tenant ids (hidden by the query filter).</summary>
    Task<ContractDocument?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all documents for the given <paramref name="contractId"/> for the current tenant,
    /// paginated via the shared <c>(CreatedAt, Id)</c> cursor helper.
    /// </summary>
    Task<PagedResult<ContractDocument>> ListByContractAsync(
        Guid contractId,
        PageRequest request,
        CancellationToken cancellationToken = default);
}
