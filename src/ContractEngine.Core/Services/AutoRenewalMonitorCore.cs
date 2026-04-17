using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContractEngine.Core.Services;

/// <summary>
/// Core auto-renewal logic (PRD §7). Separated from the Quartz job shell for testability.
/// Scans for Expiring contracts with <c>auto_renewal=true</c> and <c>end_date</c> in the past,
/// transitions each through Expiring → Renewed → Active, creates a new contract version, and
/// generates an <see cref="AlertType.AutoRenewalWarning"/> alert.
///
/// <para>Phase 3 side-effect: after each successful renewal commit, a <c>contract.renewed</c>
/// event is published to the Financial Compliance Ledger. Publisher failures are caught and logged
/// — missing ledger entries MUST NOT roll back the renewal itself (ledger is trailing audit).</para>
/// </summary>
public sealed class AutoRenewalMonitorCore
{
    private readonly IAutoRenewalStore _store;
    private readonly IDeadlineAlertWriter _alertWriter;
    private readonly IComplianceEventPublisher _compliancePublisher;
    private readonly ILogger<AutoRenewalMonitorCore> _logger;

    public AutoRenewalMonitorCore(
        IAutoRenewalStore store,
        IDeadlineAlertWriter alertWriter,
        IComplianceEventPublisher? compliancePublisher = null,
        ILogger<AutoRenewalMonitorCore>? logger = null)
    {
        _store = store;
        _alertWriter = alertWriter;
        // Optional to keep legacy test ctors compiling. Production DI always resolves a non-null
        // publisher (real NATS client or no-op stub) and a real logger.
        _compliancePublisher = compliancePublisher ?? new NullCompliancePublisher();
        _logger = logger ?? NullLogger<AutoRenewalMonitorCore>.Instance;
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

                // Phase 3 — emit contract.renewed AFTER the renewal commit. Failures are swallowed
                // and logged — a missed ledger entry must never roll back the renewal itself.
                try
                {
                    var envelope = new ComplianceEventEnvelope(
                        EventType: "contract.renewed",
                        TenantId: contract.TenantId,
                        Timestamp: DateTimeOffset.UtcNow,
                        Payload: new
                        {
                            contract_id = contract.Id,
                            tenant_id = contract.TenantId,
                            old_end_date = oldEndDate,
                            new_end_date = newEndDate,
                            renewal_period_months = periodMonths,
                            version_number = version.VersionNumber,
                        });
                    await _compliancePublisher
                        .PublishAsync("contract.renewed", envelope, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Compliance Ledger publish of contract.renewed for {ContractId} failed — continuing",
                        contract.Id);
                }

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

    /// <summary>
    /// Fallback <see cref="IComplianceEventPublisher"/> used only when a legacy test ctor omits the
    /// publisher. Behaves identically to <c>NoOpCompliancePublisher</c> (returns false, never
    /// throws) so the renewal path stays intact.
    /// </summary>
    private sealed class NullCompliancePublisher : IComplianceEventPublisher
    {
        public Task<bool> PublishAsync(
            string subject,
            ComplianceEventEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}

/// <summary>Result envelope for the auto-renewal scan.</summary>
public sealed record AutoRenewalResult
{
    public int ContractsRenewed { get; init; }
    public int Errors { get; init; }
}
