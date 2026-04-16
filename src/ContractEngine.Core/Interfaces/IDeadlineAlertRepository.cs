using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using ContractEngine.Core.Services;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the <c>deadline_alerts</c> table. Defined in Core so the service, the future
/// scanner job (Batch 016), and the endpoints can depend on it without a direct reference to
/// Infrastructure / EF Core. Tenant scoping is enforced at the query-filter level — callers never
/// pass <c>tenant_id</c> explicitly.
///
/// <para>The lookup surface includes a <see cref="FindByKeyAsync"/> helper that the service uses
/// to realise the "at most one alert per (obligation_id, alert_type, days_remaining)" idempotency
/// contract without relying on a DB UNIQUE index (PRD §4.9 doesn't require one).</para>
/// </summary>
public interface IDeadlineAlertRepository
{
    Task AddAsync(DeadlineAlert alert, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>null</c> for missing or cross-tenant ids (hidden by the query filter).</summary>
    Task<DeadlineAlert?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the existing alert row for the <c>(obligation_id, alert_type, days_remaining)</c>
    /// idempotency key if one already exists, else <c>null</c>. <paramref name="daysRemaining"/>
    /// accepts <c>null</c> so overdue / conflict alerts (which have no forward horizon) still have
    /// a deterministic lookup.
    /// </summary>
    Task<DeadlineAlert?> FindByKeyAsync(
        Guid obligationId,
        AlertType alertType,
        int? daysRemaining,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(DeadlineAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists alerts for the current tenant, narrowed by the supplied <paramref name="filters"/>.
    /// Pagination uses the shared <c>(CreatedAt, Id)</c> cursor helper.
    /// </summary>
    Task<PagedResult<DeadlineAlert>> ListAsync(
        AlertFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-acknowledges every unacknowledged alert for the current tenant, optionally narrowed by
    /// <paramref name="contractId"/> and/or <paramref name="alertType"/>. Returns the number of
    /// rows updated. Implementation uses EF Core's <c>ExecuteUpdateAsync</c> so the update runs as
    /// a single SQL <c>UPDATE</c> round-trip.
    /// </summary>
    Task<int> BulkAcknowledgeAsync(
        Guid tenantId,
        string acknowledgedBy,
        Guid? contractId,
        AlertType? alertType,
        CancellationToken cancellationToken = default);
}
