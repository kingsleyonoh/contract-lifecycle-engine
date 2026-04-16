using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>counterparties</c> table. Defined in Core so <see cref="Services.CounterpartyService"/>
/// and any future CLI/seed code can depend on it without a direct reference to Infrastructure /
/// EF Core. All tenant scoping is enforced at the EF Core query-filter level (see
/// <c>ContractDbContext.OnModelCreating</c>) — the repository never has to pass tenant ids
/// explicitly.
/// </summary>
public interface ICounterpartyRepository
{
    Task AddAsync(Counterparty counterparty, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single counterparty by id. Returns <c>null</c> when the id belongs to a
    /// different tenant — the global query filter hides cross-tenant rows without raising.
    /// </summary>
    Task<Counterparty?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Counterparty counterparty, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists counterparties for the current tenant, optionally narrowed by case-insensitive
    /// <paramref name="searchTerm"/> match on <c>name</c> and an exact <paramref name="industry"/>
    /// match. Pagination uses the shared cursor helper (<c>CodebaseContext</c> Key Patterns §2).
    /// </summary>
    Task<PagedResult<Counterparty>> ListAsync(
        string? searchTerm,
        string? industry,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the number of contracts for the given counterparty. Reserved API for Batch 007
    /// when the <c>Contract</c> entity ships — returns 0 today.
    /// </summary>
    Task<int> GetContractCountAsync(Guid counterpartyId, CancellationToken cancellationToken = default);
}
