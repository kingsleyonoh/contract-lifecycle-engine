namespace ContractEngine.Core.Abstractions;

/// <summary>
/// Marker interface for entities that are isolated per tenant. Any entity implementing this
/// interface automatically receives a global EF Core query filter that restricts queries to the
/// current <see cref="ITenantContext.TenantId"/>. See <c>ContractDbContext.ApplyTenantQueryFilter</c>.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
