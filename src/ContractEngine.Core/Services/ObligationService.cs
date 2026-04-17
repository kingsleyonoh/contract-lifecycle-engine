using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
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
///
/// <para>Partial class split: CRUD + queries in this file; transition methods in
/// <c>ObligationService.Transitions.cs</c>; archive cascade in
/// <c>ObligationService.Cascade.cs</c>.</para>
/// </summary>
public sealed partial class ObligationService
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

    // ── Private helpers shared across partial files ──────────────────────────

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
