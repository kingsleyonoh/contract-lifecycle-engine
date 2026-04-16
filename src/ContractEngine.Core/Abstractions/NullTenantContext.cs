namespace ContractEngine.Core.Abstractions;

/// <summary>
/// Default <see cref="ITenantContext"/> implementation used when no tenant has been resolved.
/// Applies to public endpoints (registration, health, webhooks pre-verification) and to
/// contexts built before <c>TenantResolutionMiddleware</c> ships (Batch 003). Downstream code
/// that requires a tenant MUST check <see cref="IsResolved"/> before use.
/// </summary>
public sealed class NullTenantContext : ITenantContext
{
    public Guid? TenantId => null;
    public bool IsResolved => false;
}
