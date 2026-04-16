using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Exceptions;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Services;

/// <summary>
/// Tenant-scoped CRUD + state-transition orchestration over the <c>obligations</c> and
/// <c>obligation_events</c> tables. PRD §5.3.
///
/// <para>Event-sourcing contract: creation emits NO event — events fire only on status
/// transitions. Every <see cref="ConfirmAsync"/> / <see cref="DismissAsync"/> /
/// <see cref="FulfillAsync"/> / <see cref="WaiveAsync"/> / <see cref="DisputeAsync"/> /
/// <see cref="ResolveDisputeAsync"/> call writes exactly one <see cref="ObligationEvent"/> row with
/// the from/to status, actor, and optional reason. The event log is INSERT-only (enforced at the
/// repository interface level).</para>
///
/// <para>Actor convention: endpoints pass <c>"user:{tenantId}"</c> until a real user-auth
/// model ships. Background jobs use <c>"scheduler:{job_name}"</c>. Bulk cascades use
/// <c>"system"</c> or <c>"system:archive_cascade"</c> (matches PRD §4.7).</para>
///
/// <para>Recurring-obligation fulfilment (PRD §5.3): when a recurring obligation
/// (<see cref="ObligationRecurrence.Monthly"/>, <see cref="ObligationRecurrence.Quarterly"/>,
/// <see cref="ObligationRecurrence.Annually"/>) is fulfilled, the service spawns a new Active
/// obligation with <c>next_due_date</c> advanced by the recurrence interval. The spawn writes a
/// single event on the new row describing the provenance — this is the one exception to the
/// "creation emits no event" rule because the provenance is important for audit.</para>
///
/// <para>Archive cascade (PRD §5.1): <see cref="ExpireDueToContractArchiveAsync"/> is invoked by
/// <c>ContractService.ArchiveAsync</c> after a contract transitions to Archived; it expires every
/// non-terminal obligation on the contract with <c>actor = "system:archive_cascade"</c>.</para>
/// </summary>
public sealed class ObligationService
{
    private readonly IObligationRepository _obligationRepository;
    private readonly IObligationEventRepository _eventRepository;
    private readonly IContractRepository _contractRepository;
    private readonly ObligationStateMachine _stateMachine;
    private readonly ITenantContext _tenantContext;

    public ObligationService(
        IObligationRepository obligationRepository,
        IObligationEventRepository eventRepository,
        IContractRepository contractRepository,
        ObligationStateMachine stateMachine,
        ITenantContext tenantContext)
    {
        _obligationRepository = obligationRepository;
        _eventRepository = eventRepository;
        _contractRepository = contractRepository;
        _stateMachine = stateMachine;
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// Creates a manual <see cref="ObligationStatus.Pending"/> obligation under the resolved tenant.
    /// Validates the parent contract exists (404 via <see cref="KeyNotFoundException"/> otherwise).
    /// Does NOT write an <see cref="ObligationEvent"/> — events fire only on transitions.
    /// </summary>
    public async Task<Obligation> CreateAsync(
        CreateObligationRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required", nameof(actor));
        }

        var tenantId = RequireTenantId();

        var contract = await _contractRepository.GetByIdAsync(request.ContractId, cancellationToken);
        if (contract is null)
        {
            throw new KeyNotFoundException(
                $"contract {request.ContractId} not found for this tenant");
        }

        var now = DateTime.UtcNow;
        var obligation = new Obligation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ContractId = request.ContractId,
            ObligationType = request.ObligationType,
            Status = ObligationStatus.Pending,
            Source = ObligationSource.Manual,
            Title = request.Title.Trim(),
            Description = Normalize(request.Description),
            ResponsibleParty = ParseResponsibleParty(request.ResponsibleParty),
            DeadlineDate = request.DeadlineDate,
            DeadlineFormula = Normalize(request.DeadlineFormula),
            Recurrence = request.Recurrence,
            // Seed next_due_date from deadline_date so the deadline scanner picks it up when the
            // obligation is confirmed. If no deadline_date (formula / recurrence only), the scanner
            // will compute it from the formula in Phase 2.
            NextDueDate = request.DeadlineDate,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency)
                ? "USD"
                : request.Currency!.Trim().ToUpperInvariant(),
            AlertWindowDays = request.AlertWindowDays ?? 30,
            GracePeriodDays = request.GracePeriodDays ?? 0,
            BusinessDayCalendar = string.IsNullOrWhiteSpace(request.BusinessDayCalendar)
                ? "US"
                : request.BusinessDayCalendar!.Trim().ToUpperInvariant(),
            ClauseReference = Normalize(request.ClauseReference),
            Metadata = request.Metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _obligationRepository.AddAsync(obligation, cancellationToken);
        // Deliberately: NO event row here. Events represent transitions, not creation.
        return obligation;
    }

    public Task<Obligation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _obligationRepository.GetByIdAsync(id, cancellationToken);

    /// <summary>
    /// Loads an obligation plus its full event timeline (ascending by created_at). Returns
    /// <c>null</c> when the obligation doesn't exist or is hidden by the tenant query filter.
    /// Implemented as two separate queries rather than an EF Include: the event timeline is an
    /// audit list — we never want EF's change tracker to see it, and keeping the reads separate
    /// lets both use <c>AsNoTracking</c> without fighting the navigation-property loader.
    /// </summary>
    public async Task<(Obligation Obligation, IReadOnlyList<ObligationEvent> Events)?> GetByIdWithEventsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var obligation = await _obligationRepository.GetByIdAsync(id, cancellationToken);
        if (obligation is null)
        {
            return null;
        }

        var events = await _eventRepository.ListAllByObligationAscAsync(id, cancellationToken);
        return (obligation, events);
    }

    public Task<PagedResult<Obligation>> ListAsync(
        ObligationFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default) =>
        _obligationRepository.ListAsync(filters, request, cancellationToken);

    /// <summary>
    /// Paginated event timeline for a single obligation. Pure delegation to the repository —
    /// Batch 013 adds this so the <c>GET /api/obligations/{id}/events</c> endpoint has a service-
    /// layer call-site without reaching into the repo directly from the endpoint.
    /// </summary>
    public Task<PagedResult<ObligationEvent>> ListEventsAsync(
        Guid obligationId,
        PageRequest request,
        CancellationToken cancellationToken = default) =>
        _eventRepository.ListByObligationAsync(obligationId, request, cancellationToken);

    /// <summary>
    /// Pending → Active. The only legal transition from Pending other than Dismissed. Writes the
    /// obligation update then the event row in one DbContext scope; EF tracks them as separate
    /// entity sets so a single SaveChanges commits both atomically.
    /// </summary>
    public Task<Obligation?> ConfirmAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            id,
            ObligationStatus.Active,
            actor,
            reason: "confirm: pending → active",
            cancellationToken);

    /// <summary>
    /// Pending → Dismissed. Terminal transition; the obligation never re-enters the flow. Reason
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
    /// Non-terminal → Fulfilled. For recurring obligations (<see cref="ObligationRecurrence.Monthly"/>,
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
            ? $"fulfill: {EnumToSnake(fromStatus.ToString())} → fulfilled"
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
    /// Creates the next Active instance of a recurring obligation after its parent is fulfilled.
    /// Copies every scheduling field from <paramref name="parent"/>, advances <c>next_due_date</c>
    /// by the recurrence interval, and writes a provenance event tying the new row back to the
    /// parent. Empty <c>FromStatus</c> on the event signals a creation event rather than a
    /// transition — the audit log still records the lineage.
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
            // Intentionally null — the spawn is scheduled by next_due_date, not deadline_date.
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
    /// Non-terminal → Waived. Terminal transition; the obligation never re-enters the flow.
    /// <paramref name="reason"/> is required so the audit log captures why the obligation was
    /// waived — waiver without a rationale is a compliance liability.
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
    /// Active → Disputed (PRD §4.6). Captures the disputing party's <paramref name="reason"/> in
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
    /// Resolves a dispute: Disputed → Active (resolution=<see cref="DisputeResolution.Stands"/>) or
    /// Disputed → Waived (resolution=<see cref="DisputeResolution.Waived"/>). Notes are optional and
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
            : $"resolve-dispute: {keyword} — {notes!.Trim()}";

        return TransitionAsync(id, targetStatus, actor, reason, cancellationToken);
    }

    /// <summary>
    /// Archive cascade (PRD §5.1). Called by <c>ContractService.ArchiveAsync</c> after the contract
    /// itself is transitioned to Archived. Expires every non-terminal obligation on the contract
    /// (Pending / Active / Upcoming / Due / Overdue / Escalated / Disputed → Expired) and writes
    /// one event per expired row with <paramref name="actor"/> (caller-supplied; conventional value
    /// is <c>"system:archive_cascade"</c>) and a metadata bag carrying the parent <c>contract_id</c>
    /// so cascade rows are easy to distinguish from organic expiries.
    ///
    /// <para>Implementation note: pages through the repository's filtered list so a contract with
    /// thousands of rows (edge case, but possible for long-running annual obligations) doesn't blow
    /// memory. The state machine filters terminal rows before attempting a transition — calling
    /// <c>EnsureTransitionAllowed</c> on a Fulfilled row would (correctly) throw, so we skip them
    /// explicitly.</para>
    /// </summary>
    public async Task ExpireDueToContractArchiveAsync(
        Guid contractId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required", nameof(actor));
        }

        var tenantId = RequireTenantId();

        // Page through in large chunks. The list filters by contract_id, so even with the 100-row
        // page cap this is at most a handful of round-trips for the vast majority of contracts.
        string? cursor = null;
        while (true)
        {
            var page = await _obligationRepository.ListAsync(
                new ObligationFilters { ContractId = contractId },
                new PageRequest { Cursor = cursor, PageSize = PageRequest.MaxPageSize },
                cancellationToken);

            foreach (var row in page.Data)
            {
                if (_stateMachine.IsTerminal(row.Status))
                {
                    continue;
                }

                var fromStatus = row.Status;
                var now = DateTime.UtcNow;
                row.Status = ObligationStatus.Expired;
                row.UpdatedAt = now;
                await _obligationRepository.UpdateAsync(row, cancellationToken);

                await _eventRepository.AddAsync(new ObligationEvent
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ObligationId = row.Id,
                    FromStatus = EnumToSnake(fromStatus.ToString()),
                    ToStatus = EnumToSnake(ObligationStatus.Expired.ToString()),
                    Actor = actor,
                    Reason = "parent contract archived",
                    Metadata = new Dictionary<string, object>
                    {
                        ["contract_id"] = contractId.ToString(),
                    },
                    CreatedAt = now,
                }, cancellationToken);
            }

            if (!page.Pagination.HasMore || string.IsNullOrWhiteSpace(page.Pagination.NextCursor))
            {
                break;
            }
            cursor = page.Pagination.NextCursor;
        }
    }

    /// <summary>
    /// Shared transition pipeline: load → enforce via state machine → update status → write event.
    /// Returns <c>null</c> when the obligation doesn't exist (404 from the endpoint). Invalid
    /// transitions bubble as <see cref="ObligationTransitionException"/> (→ 422 INVALID_TRANSITION).
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

        return existing;
    }

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }

    private static string? Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    private static ResponsibleParty ParseResponsibleParty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ResponsibleParty.Us;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "us" => ResponsibleParty.Us,
            "counterparty" => ResponsibleParty.Counterparty,
            "both" => ResponsibleParty.Both,
            // Validator gates this up-front; defensive fallback keeps the service robust.
            _ => ResponsibleParty.Us,
        };
    }

    /// <summary>
    /// Computes the next due date for a recurring obligation. <c>DateOnly.AddMonths</c> (net 7+)
    /// handles month-end edge cases correctly — e.g. Jan 31 + 1 month clamps to Feb 28/29 rather
    /// than overflowing. Annual = +12 months for the same reason. If the parent has no
    /// <c>next_due_date</c> (shouldn't happen for a confirmed recurring obligation, but defensive),
    /// we fall back to today so the scanner still picks up the spawn.
    /// </summary>
    private static DateOnly ComputeNextDueDate(DateOnly? current, ObligationRecurrence recurrence)
    {
        var baseDate = current ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return recurrence switch
        {
            ObligationRecurrence.Monthly => baseDate.AddMonths(1),
            ObligationRecurrence.Quarterly => baseDate.AddMonths(3),
            ObligationRecurrence.Annually => baseDate.AddMonths(12),
            // OneTime is filtered out by the caller; defensive fallback if that changes.
            _ => baseDate,
        };
    }

    private static string EnumToSnake(string value)
    {
        // Mirrors ObligationStatus / ContractDbContext conversion; keeps strings consistent on
        // the wire, in the DB, and in the event log.
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
/// Domain-level envelope for <see cref="ObligationService.CreateAsync"/>. Distinct from the Api
/// wire DTO (<c>ContractEngine.Api.Endpoints.Dto.CreateObligationRequest</c>) so Core stays free
/// of an Api reference. The endpoint maps the wire DTO into this record before calling the
/// service.
/// </summary>
public sealed record CreateObligationRequest
{
    public Guid ContractId { get; init; }
    public ObligationType ObligationType { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ResponsibleParty { get; init; }
    public DateOnly? DeadlineDate { get; init; }
    public string? DeadlineFormula { get; init; }
    public ObligationRecurrence? Recurrence { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public int? AlertWindowDays { get; init; }
    public int? GracePeriodDays { get; init; }
    public string? BusinessDayCalendar { get; init; }
    public string? ClauseReference { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
