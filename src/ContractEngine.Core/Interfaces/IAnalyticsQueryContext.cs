using System.Linq.Expressions;
using ContractEngine.Core.Models;
using ContractEngine.Core.Services;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the read surface used by <see cref="AnalyticsService"/>. Keeps the service in
/// Core (no Infrastructure dependency) while allowing the Infrastructure implementation to route
/// straight to the tenant-filtered <c>ContractDbContext</c>.
///
/// <para>Every method is <b>tenant-scoped</b> — the concrete implementation relies on the shared
/// global query filter so callers never have to pass the tenant id.</para>
/// </summary>
public interface IAnalyticsQueryContext
{
    Task<int> CountContractsAsync(
        Expression<Func<Contract, bool>> predicate,
        CancellationToken cancellationToken = default);

    Task<int> CountObligationsAsync(
        Expression<Func<Obligation, bool>> predicate,
        CancellationToken cancellationToken = default);

    Task<int> CountAlertsAsync(
        Expression<Func<DeadlineAlert, bool>> predicate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObligationsByTypeGroup>> GroupObligationsByTypeAndStatusAsync(
        DateTime createdAtStart,
        DateTime createdAtEndExclusive,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContractValueGroup>> GroupContractValueAsync(
        Guid? counterpartyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadlineCalendarItem>> ListDeadlineCalendarAsync(
        DateOnly from,
        DateOnly to,
        int hardCap,
        CancellationToken cancellationToken = default);
}
