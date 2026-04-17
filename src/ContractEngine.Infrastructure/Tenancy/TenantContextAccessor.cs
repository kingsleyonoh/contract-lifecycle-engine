using ContractEngine.Core.Abstractions;

namespace ContractEngine.Infrastructure.Tenancy;

/// <summary>
/// Scoped concrete implementation of <see cref="ITenantContext"/> that the
/// <c>TenantResolutionMiddleware</c> writes to for each request. Consumers depend on the
/// read-only <see cref="ITenantContext"/>; only the middleware holds the write-capable
/// <see cref="TenantContextAccessor"/> reference (via DI, because the same instance is aliased to
/// both interfaces — see <c>ServiceRegistration.AddContractEngineInfrastructure</c>).
/// </summary>
public sealed class TenantContextAccessor : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public bool IsResolved => TenantId is not null;

    public void Resolve(Guid tenantId)
    {
        TenantId = tenantId;
    }

    public void Clear()
    {
        TenantId = null;
    }
}
