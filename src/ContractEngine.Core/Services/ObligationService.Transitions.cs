using ContractEngine.Core.Enums;
using ContractEngine.Core.Models;

namespace ContractEngine.Core.Services;

/// <summary>
/// State-machine transitions: confirm, dismiss, fulfill, waive, dispute, resolve-dispute, and the
/// shared <see cref="TransitionAsync"/> pipeline. Recurring-obligation spawn logic lives here
/// because it fires as a side-effect of <see cref="FulfillAsync"/>.
/// </summary>
public sealed partial class ObligationService
{
    /// <summary>
    /// Pending -> Active. The only legal transition from Pending other than Dismissed. Writes the
    /// obligation update then the event row in one DbContext scope; EF tracks them as separate
    /// entity sets so a single SaveChanges commits both atomically.
    ///
    /// <para>Phase 3 side-effect: when the obligation's type is <see cref="ObligationType.Payment"/>,
    /// a purchase-order is emitted to the Invoice Recon engine AFTER the DB commit. Failures are
    /// caught + logged — confirmation itself must never roll back over a missed PO.</para>
    /// </summary>
    public async Task<Obligation?> ConfirmAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var updated = await TransitionAsync(
            id,
            ObligationStatus.Active,
            actor,
            reason: "confirm: pending \u2192 active",
            cancellationToken).ConfigureAwait(false);

        if (updated is not null)
        {
            // Best-effort dispatch — never rolls back. EmitInvoiceReconAsync itself swallows
            // + logs any exception from the recon client.
            await EmitInvoiceReconAsync(updated, updated.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Pending -> Dismissed. Terminal transition; the obligation never re-enters the flow. Reason
    /// is captured verbatim on the event so the audit log explains WHY the row was dismissed.
    /// </summary>
    public Task<Obligation?> DismissAsync(
        Guid id,
        string? reason,
        string actor,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            ObligationStatus.Dismissed,
            actor,
            reason: reason,
            cancellationToken);

    /// <summary>
    /// Non-terminal -> Fulfilled. For recurring obligations (<see cref="ObligationRecurrence.Monthly"/>,
    /// <see cref="ObligationRecurrence.Quarterly"/>, <see cref="ObligationRecurrence.Annually"/>) a
    /// fresh Active obligation is spawned with <c>next_due_date</c> advanced by the recurrence
    /// interval, mirroring every other scheduling field from the parent. The spawn writes one
    /// auto-generated event (<c>actor = "system"</c>, reason captures the parent id) so the audit
    /// log explains the provenance of the new row.
    /// </summary>
    public async Task<Obligation?> FulfillAsync(
        Guid id,
        string? notes,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required", nameof(actor));
        }

        var tenantId = RequireTenantId();

        var existing = await _obligationRepository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var fromStatus = existing.Status;
        _stateMachine.EnsureTransitionAllowed(fromStatus, ObligationStatus.Fulfilled);

        var now = DateTime.UtcNow;
        existing.Status = ObligationStatus.Fulfilled;
        existing.UpdatedAt = now;
        await _obligationRepository.UpdateAsync(existing, cancellationToken);

        var reason = string.IsNullOrWhiteSpace(notes)
            ? $"fulfill: {EnumToSnake(fromStatus.ToString())} \u2192 fulfilled"
            : $"fulfill: {notes!.Trim()}";
        await _eventRepository.AddAsync(new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ObligationId = existing.Id,
            FromStatus = EnumToSnake(fromStatus.ToString()),
            ToStatus = EnumToSnake(ObligationStatus.Fulfilled.ToString()),
            Actor = actor,
            Reason = reason,
            CreatedAt = now,
        }, cancellationToken);

        // Recurring? Spawn the next instance with next_due_date advanced by the interval.
        if (existing.Recurrence is { } rec && rec != ObligationRecurrence.OneTime)
        {
            await SpawnRecurringChildAsync(existing, rec, tenantId, now, cancellationToken);
        }

        return existing;
    }

    /// <summary>
    /// Non-terminal -> Waived. Terminal transition; the obligation never re-enters the flow.
    /// <paramref name="reason"/> is required so the audit log captures why the obligation was
    /// waived -- waiver without a rationale is a compliance liability.
    /// </summary>
    public Task<Obligation?> WaiveAsync(
        Guid id,
        string reason,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required to waive an obligation", nameof(reason));
        }

        return TransitionAsync(
            id,
            ObligationStatus.Waived,
            actor,
            reason: reason.Trim(),
            cancellationToken);
    }

    /// <summary>
    /// Active -> Disputed (PRD 4.6). Captures the disputing party's <paramref name="reason"/> in
    /// the event log so the audit trail explains WHY the obligation was contested.
    /// </summary>
    public Task<Obligation?> DisputeAsync(
        Guid id,
        string reason,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required to dispute an obligation", nameof(reason));
        }

        return TransitionAsync(
            id,
            ObligationStatus.Disputed,
            actor,
            reason: reason.Trim(),
            cancellationToken);
    }

    /// <summary>
    /// Resolves a dispute: Disputed -> Active (resolution=<see cref="DisputeResolution.Stands"/>) or
    /// Disputed -> Waived (resolution=<see cref="DisputeResolution.Waived"/>). Notes are optional and
    /// appended to the event reason for auditability.
    /// </summary>
    public Task<Obligation?> ResolveDisputeAsync(
        Guid id,
        DisputeResolution resolution,
        string? notes,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var targetStatus = resolution switch
        {
            DisputeResolution.Stands => ObligationStatus.Active,
            DisputeResolution.Waived => ObligationStatus.Waived,
            _ => throw new ArgumentException(
                $"unknown dispute resolution '{resolution}'", nameof(resolution)),
        };

        var keyword = resolution == DisputeResolution.Stands ? "stands" : "waived";
        var reason = string.IsNullOrWhiteSpace(notes)
            ? $"resolve-dispute: {keyword}"
            : $"resolve-dispute: {keyword} \u2014 {notes!.Trim()}";

        return TransitionAsync(id, targetStatus, actor, reason, cancellationToken);
    }

    /// <summary>
    /// Creates the next Active instance of a recurring obligation after its parent is fulfilled.
    /// Copies every scheduling field from <paramref name="parent"/>, advances <c>next_due_date</c>
    /// by the recurrence interval, and writes a provenance event tying the new row back to the
    /// parent. Empty <c>FromStatus</c> on the event signals a creation event rather than a
    /// transition -- the audit log still records the lineage.
    /// </summary>
    private async Task SpawnRecurringChildAsync(
        Obligation parent,
        ObligationRecurrence rec,
        Guid tenantId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var nextDue = ComputeNextDueDate(parent.NextDueDate ?? parent.DeadlineDate, rec);
        var spawn = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = parent.ContractId,
            ObligationType = parent.ObligationType,
            Status = ObligationStatus.Active,
            Title = parent.Title,
            Description = parent.Description,
            ResponsibleParty = parent.ResponsibleParty,
            // Intentionally null -- the spawn is scheduled by next_due_date, not deadline_date.
            DeadlineDate = null,
            DeadlineFormula = parent.DeadlineFormula,
            Recurrence = parent.Recurrence,
            NextDueDate = nextDue,
            Amount = parent.Amount,
            Currency = parent.Currency,
            AlertWindowDays = parent.AlertWindowDays,
            GracePeriodDays = parent.GracePeriodDays,
            BusinessDayCalendar = parent.BusinessDayCalendar,
            Source = parent.Source,
            ExtractionJobId = parent.ExtractionJobId,
            ClauseReference = parent.ClauseReference,
            Metadata = parent.Metadata is null
                ? null
                : new Dictionary<string, object>(parent.Metadata),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _obligationRepository.AddAsync(spawn, cancellationToken);

        await _eventRepository.AddAsync(new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ObligationId = spawn.Id,
            FromStatus = string.Empty,
            ToStatus = EnumToSnake(ObligationStatus.Active.ToString()),
            Actor = "system",
            Reason = $"auto-created from fulfilled parent (recurring): {parent.Id}",
            Metadata = new Dictionary<string, object>
            {
                ["parent_id"] = parent.Id.ToString(),
                ["recurrence"] = EnumToSnake(rec.ToString()),
            },
            CreatedAt = now,
        }, cancellationToken);
    }

    /// <summary>
    /// Shared transition pipeline: load -> enforce via state machine -> update status -> write event.
    /// Returns <c>null</c> when the obligation doesn't exist (404 from the endpoint). Invalid
    /// transitions bubble as <see cref="Exceptions.ObligationTransitionException"/> (-> 422 INVALID_TRANSITION).
    /// </summary>
    private async Task<Obligation?> TransitionAsync(
        Guid id,
        ObligationStatus targetStatus,
        string actor,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required", nameof(actor));
        }

        var tenantId = RequireTenantId();

        var existing = await _obligationRepository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var fromStatus = existing.Status;
        _stateMachine.EnsureTransitionAllowed(fromStatus, targetStatus);

        existing.Status = targetStatus;
        existing.UpdatedAt = DateTime.UtcNow;
        await _obligationRepository.UpdateAsync(existing, cancellationToken);

        var evt = new ObligationEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ObligationId = existing.Id,
            FromStatus = EnumToSnake(fromStatus.ToString()),
            ToStatus = EnumToSnake(targetStatus.ToString()),
            Actor = actor,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
        };
        await _eventRepository.AddAsync(evt, cancellationToken);

        // Phase 3 — fire-and-forget ecosystem dispatch AFTER the event row commits. Failures
        // are swallowed + logged inside the helper so a missed notification or ledger entry
        // never rolls back the transition that produced them.
        await EmitTransitionSideEffectsAsync(existing, fromStatus, targetStatus, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return existing;
    }
}
