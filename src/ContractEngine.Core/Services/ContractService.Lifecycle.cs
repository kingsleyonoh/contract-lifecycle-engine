using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;
using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Core.Services;

/// <summary>
/// Lifecycle transitions for <see cref="ContractService"/> — Activate / Terminate / Archive — plus
/// the compliance-event emit helper. Split into a partial file so the main service focuses on
/// CRUD + request resolution and this slice owns the PRD §4.3 state-machine enforcement.
/// </summary>
public sealed partial class ContractService
{
    /// <summary>
    /// Transitions a <see cref="ContractStatus.Draft"/> contract to
    /// <see cref="ContractStatus.Active"/>. Validates that <c>effective_date &lt;= today</c> and
    /// (when set) <c>today &lt;= end_date</c>, per PRD §5.1 edge case.
    /// </summary>
    public async Task<Contract?> ActivateAsync(
        Guid id,
        DateOnly? effectiveDate,
        DateOnly? endDate,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status != ContractStatus.Draft)
        {
            throw InvalidTransition(existing.Status, ContractStatus.Active);
        }

        if (effectiveDate is { } eff)
        {
            existing.EffectiveDate = eff;
        }
        if (endDate is { } end)
        {
            existing.EndDate = end;
        }

        if (existing.EffectiveDate is null)
        {
            throw new InvalidOperationException("effective_date is required to activate a contract");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (existing.EffectiveDate > today)
        {
            throw new InvalidOperationException(
                "effective_date must be on or before today to activate the contract");
        }
        if (existing.EndDate is { } configuredEnd && configuredEnd < today)
        {
            throw new InvalidOperationException(
                "end_date must be on or after today to activate the contract");
        }

        existing.Status = ContractStatus.Active;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Transitions an active/expiring contract to <see cref="ContractStatus.Terminated"/>.
    /// <paramref name="reason"/> is stored in <see cref="Contract.Metadata"/> under
    /// <c>termination_reason</c>; <paramref name="terminationDate"/>, when supplied, overrides
    /// <see cref="Contract.EndDate"/> so downstream reports show the real end.
    /// </summary>
    public async Task<Contract?> TerminateAsync(
        Guid id,
        string reason,
        DateOnly? terminationDate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required to terminate a contract", nameof(reason));
        }

        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status is not (ContractStatus.Active or ContractStatus.Expiring))
        {
            throw InvalidTransition(existing.Status, ContractStatus.Terminated);
        }

        var metadata = existing.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(existing.Metadata);
        metadata["termination_reason"] = reason.Trim();
        if (terminationDate is { } td)
        {
            metadata["termination_date"] = td.ToString("yyyy-MM-dd");
            existing.EndDate = td;
        }
        existing.Metadata = metadata;

        existing.Status = ContractStatus.Terminated;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);

        await EmitContractTerminatedAsync(existing, reason.Trim(), terminationDate, cancellationToken);

        return existing;
    }

    /// <summary>
    /// Transitions a terminal (Terminated / Expired) OR an untouched Draft contract to
    /// <see cref="ContractStatus.Archived"/>. Archiving from Active / Expiring is refused — use
    /// terminate first. Non-terminal obligations are cascade-expired via
    /// <see cref="ObligationService.ExpireDueToContractArchiveAsync"/>.
    /// </summary>
    public async Task<Contract?> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.Status is not (ContractStatus.Draft or ContractStatus.Terminated or ContractStatus.Expired))
        {
            throw InvalidTransition(existing.Status, ContractStatus.Archived);
        }

        existing.Status = ContractStatus.Archived;
        existing.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(existing, cancellationToken);

        // Archive cascade (PRD §5.1): expire every non-terminal obligation on this contract so the
        // deadline scanner stops surfacing alerts for a dead agreement. Delegates to the obligation
        // service so contract code stays oblivious to obligation internals (state machine, event
        // sourcing). When run from legacy call-sites that constructed ContractService without the
        // obligation service (tests pre-Batch 013), the cascade is a no-op.
        if (_obligationService is not null)
        {
            await _obligationService.ExpireDueToContractArchiveAsync(
                existing.Id,
                actor: "system:archive_cascade",
                cancellationToken);
        }

        return existing;
    }

    // Phase 3 — emit the contract.terminated compliance event after commit. The ledger is a
    // trailing audit stream and failures MUST NOT roll back the termination, so publish errors
    // are logged + swallowed.
    private async Task EmitContractTerminatedAsync(
        Contract existing,
        string reason,
        DateOnly? terminationDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = new ComplianceEventEnvelope(
                EventType: "contract.terminated",
                TenantId: existing.TenantId,
                Timestamp: DateTimeOffset.UtcNow,
                Payload: new
                {
                    contract_id = existing.Id,
                    title = existing.Title,
                    reason,
                    termination_date = terminationDate,
                });
            await _compliancePublisher
                .PublishAsync("contract.terminated", envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Compliance Ledger publish of contract.terminated for {ContractId} failed",
                existing.Id);
        }
    }

    private static ContractTransitionException InvalidTransition(ContractStatus from, ContractStatus to)
    {
        return new ContractTransitionException(from, to, ValidNextStates(from));
    }

    private static IReadOnlyList<ContractStatus> ValidNextStates(ContractStatus from) => from switch
    {
        ContractStatus.Draft => new[] { ContractStatus.Active, ContractStatus.Archived },
        ContractStatus.Active => new[] { ContractStatus.Expiring, ContractStatus.Terminated },
        ContractStatus.Expiring => new[] { ContractStatus.Renewed, ContractStatus.Expired },
        ContractStatus.Expired => new[] { ContractStatus.Archived },
        ContractStatus.Renewed => new[] { ContractStatus.Active },
        ContractStatus.Terminated => new[] { ContractStatus.Archived },
        ContractStatus.Archived => Array.Empty<ContractStatus>(),
        _ => Array.Empty<ContractStatus>(),
    };
}
