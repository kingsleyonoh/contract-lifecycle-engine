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
/// transitions. Every <see cref="ConfirmAsync"/> / <see cref="DismissAsync"/> call writes
/// exactly one <see cref="ObligationEvent"/> row with the from/to status, actor, and optional
/// reason. The event log is INSERT-only (enforced at the repository interface level).</para>
///
/// <para>Actor convention: endpoints pass <c>"user:{tenantId}"</c> until a real user-auth
/// model ships. Background jobs use <c>"scheduler:{job_name}"</c>. Bulk cascades use
/// <c>"system"</c> (matches PRD §4.7).</para>
///
/// <para>Active-state transitions (fulfill / waive / dispute / resolve-dispute) and the
/// paginated events-list endpoint are deferred to Batch 013. Only Pending-state moves land
/// in Batch 012 so the extract-then-confirm flow can be exercised end-to-end first.</para>
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
