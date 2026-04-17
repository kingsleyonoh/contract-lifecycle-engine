namespace ContractEngine.Core.Enums;

/// <summary>
/// Classification of a <see cref="Models.DeadlineAlert"/>. Persisted as a snake_case lowercase
/// string via the EF value converter on <c>deadline_alerts.alert_type</c> and serialises the same
/// way on the wire via the global <c>JsonStringEnumConverter(SnakeCaseLower)</c>.
///
/// <para>PRD §4.9 defines the CHECK constraint values:</para>
/// <list type="bullet">
///   <item><c>deadline_approaching</c> — scheduled alert N business days before an obligation due
///     date (90/30/14/7/1 by default; configurable via ALERT_WINDOWS_DAYS).</item>
///   <item><c>contract_expiring</c> — contract end_date is approaching (same windows as above).</item>
///   <item><c>obligation_overdue</c> — deadline passed without fulfilment.</item>
///   <item><c>auto_renewal_warning</c> — auto-renewing contract is within the renewal notice
///     window so the tenant can still opt out.</item>
///   <item><c>contract_conflict</c> — cross-contract analysis (Phase 2) detected clashing terms.</item>
/// </list>
/// </summary>
public enum AlertType
{
    DeadlineApproaching = 0,
    ContractExpiring = 1,
    ObligationOverdue = 2,
    AutoRenewalWarning = 3,
    ContractConflict = 4,
}
