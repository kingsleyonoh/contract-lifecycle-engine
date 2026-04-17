using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Integrations.InvoiceRecon;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly INotificationPublisher _notificationPublisher;
    private readonly IComplianceEventPublisher _compliancePublisher;
    private readonly IInvoiceReconClient _invoiceReconClient;
    private readonly ITenantRepository? _tenantRepository;
    private readonly ILogger<ObligationService> _logger;

    public ObligationService(
        IObligationRepository obligationRepository,
        IObligationEventRepository eventRepository,
        IContractRepository contractRepository,
        ObligationStateMachine stateMachine,
        ITenantContext tenantContext,
        INotificationPublisher? notificationPublisher = null,
        IComplianceEventPublisher? compliancePublisher = null,
        IInvoiceReconClient? invoiceReconClient = null,
        ITenantRepository? tenantRepository = null,
        ILogger<ObligationService>? logger = null)
    {
        _obligationRepository = obligationRepository;
        _eventRepository = eventRepository;
        _contractRepository = contractRepository;
        _stateMachine = stateMachine;
        _tenantContext = tenantContext;
        // All Phase-3 ecosystem deps are optional so legacy test ctors keep compiling. Production
        // DI always resolves real instances (or the no-op stubs from ServiceRegistration).
        _notificationPublisher = notificationPublisher ?? new NullNotificationPublisher();
        _compliancePublisher = compliancePublisher ?? new NullCompliancePublisher();
        _invoiceReconClient = invoiceReconClient ?? new NullInvoiceReconClient();
        _tenantRepository = tenantRepository;
        _logger = logger ?? NullLogger<ObligationService>.Instance;
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

    /// <summary>
    /// Emits Notification Hub + Compliance Ledger signals for an obligation transition.
    /// Called from <c>TransitionAsync</c> AFTER the DB commit so a failed dispatch never rolls
    /// back the status change. Internal exceptions are caught and logged — fire-and-forget per
    /// PRD §5.6b/c.
    /// </summary>
    private async Task EmitTransitionSideEffectsAsync(
        Obligation obligation,
        ObligationStatus fromStatus,
        ObligationStatus toStatus,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Notification Hub — escalate + overdue get a dedicated template.
        var notifyEventType = toStatus switch
        {
            ObligationStatus.Overdue => "obligation.overdue",
            ObligationStatus.Escalated => "obligation.escalated",
            _ => null,
        };

        if (notifyEventType is not null)
        {
            try
            {
                var payload = new
                {
                    tenant_id = tenantId,
                    obligation_id = obligation.Id,
                    contract_id = obligation.ContractId,
                    title = obligation.Title,
                    from_status = EnumToSnake(fromStatus.ToString()),
                    to_status = EnumToSnake(toStatus.ToString()),
                    next_due_date = obligation.NextDueDate,
                    amount = obligation.Amount,
                    currency = obligation.Currency,
                };
                await _notificationPublisher
                    .PublishEventAsync(notifyEventType, payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Notification Hub dispatch of {EventType} for obligation {ObligationId} failed",
                    notifyEventType,
                    obligation.Id);
            }
        }

        // Compliance Ledger — obligation breach = overdue or escalated.
        if (toStatus is ObligationStatus.Overdue or ObligationStatus.Escalated)
        {
            try
            {
                var envelope = new ComplianceEventEnvelope(
                    EventType: "contract.obligation.breached",
                    TenantId: tenantId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Payload: new
                    {
                        obligation_id = obligation.Id,
                        contract_id = obligation.ContractId,
                        title = obligation.Title,
                        from_status = EnumToSnake(fromStatus.ToString()),
                        to_status = EnumToSnake(toStatus.ToString()),
                        amount = obligation.Amount,
                        currency = obligation.Currency,
                    });
                await _compliancePublisher
                    .PublishAsync("contract.obligation.breached", envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Compliance Ledger publish of obligation.breached for {ObligationId} failed",
                    obligation.Id);
            }
        }
    }

    /// <summary>
    /// Emits a purchase-order creation to the Invoice Recon engine when an obligation of type
    /// <see cref="ObligationType.Payment"/> is confirmed. No-op for non-payment obligations.
    /// Failures are caught + logged — the confirmation itself has already committed.
    /// </summary>
    private async Task EmitInvoiceReconAsync(
        Obligation obligation,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (obligation.ObligationType != ObligationType.Payment)
        {
            return;
        }

        // Need the tenant's API key to forward as X-Tenant-API-Key. Without it the real recon
        // client would reject the call, so we skip cleanly — the integration treats missing
        // credentials as "not enabled" rather than an error.
        if (_tenantRepository is null)
        {
            return;
        }

        try
        {
            var tenant = await _tenantRepository
                .GetByIdAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            // ApiKeyHash is stored, not the plaintext — forwarding the hash lets the recon engine
            // verify the tenant without the plaintext key ever leaving the Contract Engine.
            var tenantKey = tenant?.ApiKeyHash;
            if (string.IsNullOrWhiteSpace(tenantKey))
            {
                return;
            }

            var request = new PurchaseOrderRequest(
                ContractId: obligation.ContractId,
                ObligationId: obligation.Id,
                Amount: obligation.Amount ?? 0m,
                Currency: obligation.Currency ?? "USD",
                DueDate: obligation.NextDueDate ?? obligation.DeadlineDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                Counterparty: null,
                Description: obligation.Title);
            await _invoiceReconClient
                .CreatePurchaseOrderAsync(tenantKey, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Invoice Recon PO create for confirmed obligation {ObligationId} failed",
                obligation.Id);
        }
    }

    private sealed class NullNotificationPublisher : INotificationPublisher
    {
        public Task<Integrations.Notifications.NotificationDispatchResult> PublishEventAsync(
            string eventType,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Integrations.Notifications.NotificationDispatchResult(Dispatched: false));
    }

    private sealed class NullCompliancePublisher : IComplianceEventPublisher
    {
        public Task<bool> PublishAsync(
            string subject,
            ComplianceEventEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NullInvoiceReconClient : IInvoiceReconClient
    {
        public Task<PurchaseOrderResult> CreatePurchaseOrderAsync(
            string tenantApiKey,
            PurchaseOrderRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PurchaseOrderResult(Created: false));
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
