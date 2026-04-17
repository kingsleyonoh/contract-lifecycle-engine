using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Cross-tenant read + write surface for the <c>AutoRenewalMonitorCore</c>. Lives in Core so
/// the auto-renewal logic can be unit-tested without a DbContext. The real implementation in
/// Infrastructure uses <c>IgnoreQueryFilters()</c> to bypass the tenant query filter (the job
/// runs without a resolved tenant).
/// </summary>
public interface IAutoRenewalStore
{
    /// <summary>
    /// Returns every contract with <c>status = Expiring</c> AND <c>auto_renewal = true</c>
    /// AND <c>end_date &lt; today</c> across all tenants.
    /// </summary>
    Task<IReadOnlyList<Contract>> LoadAutoRenewalCandidatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the renewed contract (status=Active, new end_date) and the auto-generated
    /// version row in one DB round-trip.
    /// </summary>
    Task SaveRenewalAsync(
        Contract contract,
        ContractVersion version,
        CancellationToken cancellationToken = default);
}
