using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Core.Services;

/// <summary>
/// Pure orchestration for the hourly deadline-scanner job. The Quartz-facing wrapper
/// <c>DeadlineScannerJob</c> (Jobs project) delegates every invocation to a single
/// <see cref="ScanAsync"/> call — all transition logic lives here so it's unit-testable without a
/// DbContext.
///
/// <para>Algorithm (PRD §5.4, §7):</para>
/// <list type="number">
///   <item>Load every non-terminal obligation with a <c>next_due_date</c>.</item>
///   <item>For each: compute <c>business_days_remaining</c> via <see cref="IBusinessDayCalculator"/>.</item>
///   <item>Decide the target status from the scanner matrix:
///     <list type="bullet">
///       <item><c>active → upcoming</c> when <c>0 &lt;= remaining &lt;= alert_window</c></item>
///       <item><c>upcoming → due</c> when <c>remaining &lt;= 0</c></item>
///       <item><c>due → overdue</c> when <c>remaining &lt; -grace_period</c></item>
///       <item><c>overdue → escalated</c> when <c>days_overdue &gt; overdue_escalation_days</c></item>
///     </list>
///   </item>
///   <item>Persist the transition via <see cref="IDeadlineScanStore.SaveObligationTransitionAsync"/>
///     (actor = <c>"scheduler:deadline_scanner"</c>).</item>
///   <item>Create a <c>deadline_approaching</c> alert when <c>remaining</c> matches a configured
///     alert window; create an <c>obligation_overdue</c> alert when <c>remaining &lt; 0</c>.</item>
///   <item>Load every active contract with <c>end_date</c>. If
///     <c>today &gt;= end_date - renewal_notice_days</c> → transition to Expiring.</item>
/// </list>
///
/// <para>Failure isolation: each obligation / contract is wrapped in a try/catch so a single
/// failure doesn't abort the whole scan. Errors are logged and counted in
/// <see cref="DeadlineScannerResult.Errors"/>.</para>
/// </summary>
public sealed class DeadlineScannerCore
{
    private readonly IDeadlineScanStore _store;
    private readonly IBusinessDayCalculator _calculator;
    private readonly IDeadlineAlertWriter _alertWriter;
    private readonly ObligationStateMachine _stateMachine;
    private readonly ILogger<DeadlineScannerCore> _logger;
    private readonly DeadlineScannerConfig _config;

    public DeadlineScannerCore(
        IDeadlineScanStore store,
        IBusinessDayCalculator calculator,
        IDeadlineAlertWriter alertWriter,
        ObligationStateMachine stateMachine,
        ILogger<DeadlineScannerCore> logger,
        DeadlineScannerConfig config)
    {
        _store = store;
        _calculator = calculator;
        _alertWriter = alertWriter;
        _stateMachine = stateMachine;
        _logger = logger;
        _config = config;
    }

    public async Task<DeadlineScannerResult> ScanAsync(CancellationToken cancellationToken)
    {
        var result = new DeadlineScannerResult();

        var obligations = await _store.LoadNonTerminalObligationsAsync(cancellationToken);
        foreach (var obligation in obligations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ObligationsScanned++;

            if (_stateMachine.IsTerminal(obligation.Status))
            {
                // Defensive: the store excludes terminal rows, but a race could slip one through.
                continue;
            }

            if (obligation.NextDueDate is null)
            {
                // Formula-only obligations are evaluated by the extraction pipeline — the scanner
                // can't compute a meaningful days-remaining here.
                continue;
            }

            try
            {
                var daysRemaining = _calculator.BusinessDaysUntil(
                    obligation.NextDueDate.Value,
                    obligation.BusinessDayCalendar,
                    obligation.TenantId);

                await ProcessObligationAsync(obligation, daysRemaining, result, cancellationToken);
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogError(ex,
                    "DeadlineScanner: obligation {ObligationId} (tenant {TenantId}) failed: {Message}",
                    obligation.Id, obligation.TenantId, ex.Message);
            }
        }

        var contracts = await _store.LoadExpiringContractsAsync(cancellationToken);
        foreach (var contract in contracts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ContractsScanned++;

            if (contract.Status != ContractStatus.Active || contract.EndDate is null)
            {
                continue;
            }

            try
            {
                var noticeDays = contract.RenewalNoticeDays > 0
                    ? contract.RenewalNoticeDays
                    : _config.DefaultRenewalNoticeDays;
                var threshold = contract.EndDate.Value.AddDays(-noticeDays);

                if (_config.Today >= threshold)
                {
                    await _store.SaveContractExpiringAsync(contract, cancellationToken);
                    result.ContractsExpired++;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogError(ex,
                    "DeadlineScanner: contract {ContractId} (tenant {TenantId}) failed: {Message}",
                    contract.Id, contract.TenantId, ex.Message);
            }
        }

        _logger.LogInformation(
            "DeadlineScanner completed: {Scanned} obligations scanned, {Transitions} transitions, {ContractsExpired} contracts expiring, {Errors} errors",
            result.ObligationsScanned, result.TransitionsApplied, result.ContractsExpired, result.Errors);
        return result;
    }

    private async Task ProcessObligationAsync(
        Obligation obligation,
        int daysRemaining,
        DeadlineScannerResult result,
        CancellationToken cancellationToken)
    {
        var nextStatus = DecideNextStatus(obligation, daysRemaining);
        if (nextStatus is { } target && target != obligation.Status)
        {
            var reason = BuildTransitionReason(obligation.Status, target, daysRemaining);
            await _store.SaveObligationTransitionAsync(
                obligation, target, "scheduler:deadline_scanner", reason, cancellationToken);
            obligation.Status = target;
            result.TransitionsApplied++;
        }

        await MaybeGenerateAlertAsync(obligation, daysRemaining, cancellationToken);
    }

    /// <summary>
    /// Applies the scanner transition matrix to an obligation with its computed
    /// <paramref name="daysRemaining"/>. Returns the target status, or <c>null</c> if no
    /// transition is warranted.
    /// </summary>
    private ObligationStatus? DecideNextStatus(Obligation obligation, int daysRemaining)
    {
        return obligation.Status switch
        {
            ObligationStatus.Active when daysRemaining >= 0 && daysRemaining <= obligation.AlertWindowDays
                => ObligationStatus.Upcoming,

            ObligationStatus.Upcoming when daysRemaining <= 0
                => ObligationStatus.Due,

            ObligationStatus.Due when daysRemaining < -obligation.GracePeriodDays
                => ObligationStatus.Overdue,

            // days_overdue past grace = (-daysRemaining) - gracePeriod
            ObligationStatus.Overdue when
                (-daysRemaining) - obligation.GracePeriodDays > _config.OverdueEscalationDays
                => ObligationStatus.Escalated,

            _ => null,
        };
    }

    private async Task MaybeGenerateAlertAsync(
        Obligation obligation,
        int daysRemaining,
        CancellationToken cancellationToken)
    {
        // Overdue obligations get a dedicated alert type. Fires every scan (the writer is
        // idempotent on (obligation_id, alert_type, days_remaining)).
        if (daysRemaining < 0)
        {
            var message = $"Obligation '{obligation.Title}' is overdue ({-daysRemaining} business days past due).";
            await _alertWriter.CreateIfNotExistsForTenantAsync(
                obligation.TenantId,
                obligation.Id,
                obligation.ContractId,
                AlertType.ObligationOverdue,
                daysRemaining,
                message,
                cancellationToken);
            return;
        }

        if (_config.AlertWindowsDays.Contains(daysRemaining))
        {
            var message = $"Obligation '{obligation.Title}' is due in {daysRemaining} business days.";
            await _alertWriter.CreateIfNotExistsForTenantAsync(
                obligation.TenantId,
                obligation.Id,
                obligation.ContractId,
                AlertType.DeadlineApproaching,
                daysRemaining,
                message,
                cancellationToken);
        }
    }

    private static string BuildTransitionReason(
        ObligationStatus from, ObligationStatus to, int daysRemaining)
    {
        var fromS = EnumToSnake(from.ToString());
        var toS = EnumToSnake(to.ToString());
        return $"deadline scanner: {fromS} → {toS} (business days remaining: {daysRemaining})";
    }

    private static string EnumToSnake(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    builder.Append('_');
                }
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}

/// <summary>
/// Configuration envelope for <see cref="DeadlineScannerCore"/>. Sourced from env vars in
/// production (ALERT_WINDOWS_DAYS, OVERDUE_ESCALATION_DAYS) and synthesised in unit tests to pin
/// deterministic windows / clock values.
/// </summary>
public sealed record DeadlineScannerConfig
{
    public int[] AlertWindowsDays { get; init; } = Array.Empty<int>();
    public int OverdueEscalationDays { get; init; } = 14;
    public int DefaultRenewalNoticeDays { get; init; } = 90;

    /// <summary>
    /// The "today" anchor used for contract expiry checks. In production this is
    /// <c>DateOnly.FromDateTime(DateTime.UtcNow)</c>; tests pin it for determinism.
    /// </summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}

/// <summary>
/// Per-run metrics surfaced by <see cref="DeadlineScannerCore.ScanAsync"/>. Logged at the end of
/// each run and returned to the caller (useful for tests + future admin endpoints).
/// </summary>
public sealed class DeadlineScannerResult
{
    public int ObligationsScanned { get; set; }
    public int TransitionsApplied { get; set; }
    public int ContractsScanned { get; set; }
    public int ContractsExpired { get; set; }
    public int Errors { get; set; }
}
