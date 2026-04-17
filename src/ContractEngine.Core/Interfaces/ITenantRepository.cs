using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>tenants</c> table. Defined in <c>Core</c> so that
/// <see cref="Services.TenantService"/> and the tenant-resolution middleware can depend on it
/// without taking a direct reference to Infrastructure / EF Core.
/// </summary>
public interface ITenantRepository
{
    Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a tenant by the SHA-256 hex of their API key. This runs BEFORE
    /// <c>TenantResolutionMiddleware</c> has populated <c>ITenantContext</c>, so the query must
    /// bypass the tenant query filter (see <c>CODEBASE_CONTEXT.md</c> Key Patterns §4).
    /// </summary>
    Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default);

    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes to a tenant row. Used by <c>PATCH /api/tenants/me</c> to flush in-memory
    /// edits back to Postgres. Implementations must update the row server-side (no upsert
    /// semantics) — caller is responsible for loading the entity and mutating it first.
    /// </summary>
    Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default);
}
