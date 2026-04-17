using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Alert-creation surface used by the <c>DeadlineScannerCore</c>. Distinct from
/// <c>DeadlineAlertService</c> because the scanner runs WITHOUT a resolved tenant — the writer
/// takes <paramref>tenantId</paramref> explicitly on every call and resolves the tenant context
/// for the nested service call internally.
///
/// <para>The service-level idempotency contract (at most one alert per
/// <c>(obligation_id, alert_type, days_remaining)</c>) is preserved by delegating to
/// <c>DeadlineAlertService.CreateIfNotExistsAsync</c> under the hood.</para>
/// </summary>
public interface IDeadlineAlertWriter
{
    Task CreateIfNotExistsForTenantAsync(
        Guid tenantId,
        Guid obligationId,
        Guid contractId,
        AlertType alertType,
        int? daysRemaining,
        string message,
        CancellationToken cancellationToken = default);
}
