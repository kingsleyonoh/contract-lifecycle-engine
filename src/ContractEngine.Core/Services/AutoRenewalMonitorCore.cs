using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// Core auto-renewal logic (PRD §7). Separated from the Quartz job shell for testability.
/// Scans for Expiring contracts with <c>auto_renewal=true</c> and <c>end_date</c> in the past,
/// transitions each through Expiring → Renewed → Active, creates a new contract version, and
/// generates an <see cref="AlertType.AutoRenewalWarning"/> alert.
/// </summary>
public sealed class AutoRenewalMonitorCore
{
    private readonly IAutoRenewalStore _store;
    private readonly IDeadlineAlertWriter _alertWriter;

    public AutoRenewalMonitorCore(
        IAutoRenewalStore store,
        IDeadlineAlertWriter alertWriter)
    {
        _store = store;
        _alertWriter = alertWriter;
    }

    public async Task<AutoRenewalResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _store.LoadAutoRenewalCandidatesAsync(cancellationToken);
        var renewed = 0;
        var errors = 0;

        foreach (var contract in candidates)
        {
            if (!contract.AutoRenewal)
            {
                continue;
            }

            try
            {
                var periodMonths = contract.AutoRenewalPeriodMonths ?? 12;
                var oldEndDate = contract.EndDate
                    ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var newEndDate = oldEndDate.AddMonths(periodMonths);

                // Transition: Expiring → Active (simplified from Expiring → Renewed → Active)
                contract.Status = ContractStatus.Active;
                contract.EndDate = newEndDate;
                contract.UpdatedAt = DateTime.UtcNow;

                var version = new ContractVersion
                {
                    Id = Guid.NewGuid(),
                    TenantId = contract.TenantId,
                    ContractId = contract.Id,
                    VersionNumber = contract.CurrentVersion + 1,
                    ChangeSummary = $"Auto-renewed for {periodMonths} months. New end date: {newEndDate:yyyy-MM-dd}",
                    CreatedBy = "system:auto_renewal",
                    CreatedAt = DateTime.UtcNow,
                };

                contract.CurrentVersion = version.VersionNumber;

                await _store.SaveRenewalAsync(contract, version, cancellationToken);

                // Generate alert
                await _alertWriter.CreateIfNotExistsForTenantAsync(
                    contract.TenantId,
                    Guid.Empty, // No specific obligation
                    contract.Id,
                    AlertType.AutoRenewalWarning,
                    daysRemaining: null,
                    $"Contract \"{contract.Title}\" was auto-renewed for {periodMonths} months. New end date: {newEndDate:yyyy-MM-dd}",
                    cancellationToken);

                renewed++;
            }
            catch (Exception)
            {
                errors++;
            }
        }

        return new AutoRenewalResult
        {
            ContractsRenewed = renewed,
            Errors = errors,
        };
    }
}

/// <summary>Result envelope for the auto-renewal scan.</summary>
public sealed record AutoRenewalResult
{
    public int ContractsRenewed { get; init; }
    public int Errors { get; init; }
}
