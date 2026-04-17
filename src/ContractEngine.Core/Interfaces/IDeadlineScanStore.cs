using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Cross-tenant read + write surface for the <c>DeadlineScannerCore</c>. Lives in Core so the
/// scanner can be unit-tested without a DbContext; the real implementation sits in Infrastructure
/// and uses <c>ContractDbContext.IgnoreQueryFilters()</c> to bypass the tenant query filter (the
/// scanner runs without a resolved tenant).
///
/// <para>Every mutation writes both the row change and a single <c>obligation_events</c> row
/// atomically — callers of this interface never touch the event log directly.</para>
/// </summary>
public interface IDeadlineScanStore
{
    /// <summary>
    /// Returns every non-terminal obligation across every tenant whose <c>next_due_date</c> is set.
    /// Terminal statuses (dismissed / fulfilled / waived / expired / pending) are excluded at the
    /// DB level so the scanner doesn't waste cycles on rows that can't transition.
    /// </summary>
    Task<IReadOnlyList<Obligation>> LoadNonTerminalObligationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every <see cref="ContractStatus.Active"/> contract across every tenant with an
    /// <c>end_date</c> set — the scanner inspects them to decide whether to transition to
    /// <see cref="ContractStatus.Expiring"/>.
    /// </summary>
    Task<IReadOnlyList<Contract>> LoadExpiringContractsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an obligation status transition and the matching <c>obligation_events</c> row in
    /// one DB round-trip. The event is written with <paramref name="actor"/> (typically
    /// <c>"scheduler:deadline_scanner"</c>) and <paramref name="reason"/> describing the
    /// transition. <paramref name="target"/> is the new status.
    /// </summary>
    Task SaveObligationTransitionAsync(
        Obligation obligation,
        ObligationStatus target,
        string actor,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions an active contract to <see cref="ContractStatus.Expiring"/>. No event log is
    /// written — contract transitions don't use event sourcing (yet). Updates
    /// <c>contracts.updated_at</c> as well.
    /// </summary>
    Task SaveContractExpiringAsync(
        Contract contract,
        CancellationToken cancellationToken = default);
}
