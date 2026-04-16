namespace ContractEngine.Core.Abstractions;

/// <summary>
/// Scoped service that carries the resolved tenant for the current request or unit of work.
/// Populated by <c>TenantResolutionMiddleware</c> (future batch) for HTTP requests, or by a
/// cross-tenant factory for jobs and seed scripts. Unresolved contexts (e.g. public endpoints,
/// webhooks before signature verification) expose <see cref="IsResolved"/> = false and
/// <see cref="TenantId"/> = null.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    bool IsResolved { get; }
}
