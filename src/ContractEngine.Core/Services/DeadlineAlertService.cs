using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContractEngine.Core.Services;

/// <summary>
/// Orchestration layer for the <c>deadline_alerts</c> table (PRD §5.4). Handles:
/// <list type="bullet">
///   <item><see cref="CreateIfNotExistsAsync"/> — idempotent creation keyed on
///     <c>(obligation_id, alert_type, days_remaining)</c>. Called by the
///     <c>DeadlineScannerJob</c> (Batch 016) and the Contract Analysis engine (Phase 2).</item>
///   <item><see cref="AcknowledgeAsync"/> — marks a single alert acknowledged.</item>
///   <item><see cref="AcknowledgeAllAsync"/> — bulk acknowledge with optional contract / type
///     filters (<c>POST /api/alerts/acknowledge-all</c>).</item>
///   <item><see cref="ListAsync"/> — pure repository delegation; kept on the service so endpoints
///     don't reach into the repo directly.</item>
/// </list>
///
/// <para>Notification-Hub dispatch is deliberately NOT performed here — that's a Phase 3
/// deliverable. The <c>notification_sent</c> column stays <c>false</c> throughout Batch 015.</para>
///
/// <para>Actor convention matches the obligation service: endpoints pass
/// <c>"user:{tenantId}"</c> until user-auth ships. Scheduler-spawned alerts will pass
/// <c>"scheduler:deadline_scanner"</c> as <paramref>acknowledgedBy</paramref> is only written by
/// the acknowledge paths (not CreateIfNotExists).</para>
/// </summary>
public sealed class DeadlineAlertService
{
    private readonly IDeadlineAlertRepository _repository;
    private readonly ITenantContext _tenantContext;
    private readonly INotificationPublisher _notificationPublisher;
    private readonly ILogger<DeadlineAlertService> _logger;

    public DeadlineAlertService(
        IDeadlineAlertRepository repository,
        ITenantContext tenantContext,
        INotificationPublisher? notificationPublisher = null,
        ILogger<DeadlineAlertService>? logger = null)
    {
        _repository = repository;
        _tenantContext = tenantContext;
        // Optional to keep legacy test ctors (pre-Batch 023) compiling. Production DI always
        // resolves a non-null publisher (real or no-op stub) and a real logger.
        _notificationPublisher = notificationPublisher ?? new NullNotificationPublisher();
        _logger = logger ?? NullLogger<DeadlineAlertService>.Instance;
    }

    /// <summary>
    /// Creates a new alert row for the current tenant if no row already exists for the
    /// <c>(obligation_id, alert_type, days_remaining)</c> key; otherwise returns the existing row
    /// unchanged. The caller must look up the parent obligation's <c>contract_id</c> beforehand —
    /// this keeps the service free of a repository cross-reference.
    /// </summary>
    public async Task<DeadlineAlert> CreateIfNotExistsAsync(
        Guid obligationId,
        Guid contractId,
        AlertType alertType,
        int? daysRemaining,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("message is required", nameof(message));
        }

        var tenantId = RequireTenantId();

        var existing = await _repository.FindByKeyAsync(
            obligationId, alertType, daysRemaining, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var alert = new DeadlineAlert
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ObligationId = obligationId,
            ContractId = contractId,
            AlertType = alertType,
            DaysRemaining = daysRemaining,
            Message = message.Trim(),
            Acknowledged = false,
            NotificationSent = false,
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.AddAsync(alert, cancellationToken);

        // Phase 3 — emit the Notification Hub event AFTER the alert has been persisted. Failures
        // from the publisher MUST NOT roll back the alert row; we catch and log. When the Hub
        // acknowledges dispatch we stamp <c>notification_sent=true</c> so the UI can distinguish
        // alerts already pushed to email/Telegram from those pending dispatch.
        try
        {
            var eventType = AlertTypeToEventType(alertType);
            var payload = new
            {
                tenant_id = tenantId,
                alert_id = alert.Id,
                obligation_id = obligationId,
                contract_id = contractId,
                alert_type = eventType,
                days_remaining = daysRemaining,
                message = alert.Message,
                created_at = alert.CreatedAt,
            };

            var result = await _notificationPublisher
                .PublishEventAsync(eventType, payload, cancellationToken)
                .ConfigureAwait(false);

            if (result.Dispatched)
            {
                alert.NotificationSent = true;
                await _repository.UpdateAsync(alert, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Notification Hub dispatch for alert {AlertId} ({AlertType}) failed — continuing",
                alert.Id,
                alertType);
        }

        return alert;
    }

    /// <summary>
    /// Maps internal <see cref="AlertType"/> enum values to canonical Notification Hub event types.
    /// The Hub's template engine keys on <c>event_type</c>; any mismatch means no template matches
    /// and the event is silently dropped — keep this table in sync with the onboarding script.
    /// </summary>
    private static string AlertTypeToEventType(AlertType alertType) => alertType switch
    {
        AlertType.DeadlineApproaching => "obligation.deadline.approaching",
        AlertType.ObligationOverdue => "obligation.overdue",
        AlertType.ContractExpiring => "contract.expiring",
        AlertType.AutoRenewalWarning => "contract.auto_renewed",
        AlertType.ContractConflict => "contract.conflict_detected",
        _ => "alert.generic",
    };

    /// <summary>
    /// Fallback <see cref="INotificationPublisher"/> used only when a legacy test ctor omits the
    /// publisher. Behaves identically to <c>NoOpNotificationPublisher</c> (returns not-dispatched,
    /// never throws) so the domain path stays intact.
    /// </summary>
    private sealed class NullNotificationPublisher : INotificationPublisher
    {
        public Task<Integrations.Notifications.NotificationDispatchResult> PublishEventAsync(
            string eventType,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Integrations.Notifications.NotificationDispatchResult(Dispatched: false));
    }

    /// <summary>
    /// Marks a single alert as acknowledged by <paramref name="acknowledgedBy"/>. Returns the
    /// updated row, or <c>null</c> when the alert doesn't exist or belongs to a different tenant
    /// (hidden by the global query filter). Re-acknowledging an already-acknowledged alert is a
    /// no-op that re-stamps <c>acknowledged_at</c> / <c>acknowledged_by</c> to the caller's values
    /// — idempotent by design so the UI doesn't need a 409 retry path.
    /// </summary>
    public async Task<DeadlineAlert?> AcknowledgeAsync(
        Guid alertId,
        string acknowledgedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(acknowledgedBy))
        {
            throw new ArgumentException("acknowledgedBy is required", nameof(acknowledgedBy));
        }

        RequireTenantId();

        var existing = await _repository.GetByIdAsync(alertId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.Acknowledged = true;
        existing.AcknowledgedAt = DateTime.UtcNow;
        existing.AcknowledgedBy = acknowledgedBy;
        await _repository.UpdateAsync(existing, cancellationToken);
        return existing;
    }

    /// <summary>
    /// Bulk-acknowledges every unacknowledged alert for the current tenant, optionally narrowed by
    /// <paramref name="contractId"/> and/or <paramref name="alertType"/>. Returns the number of
    /// rows updated. Delegates to the repository's <c>ExecuteUpdateAsync</c> path — a single round
    /// trip regardless of volume.
    /// </summary>
    public Task<int> AcknowledgeAllAsync(
        string acknowledgedBy,
        Guid? contractId = null,
        AlertType? alertType = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(acknowledgedBy))
        {
            throw new ArgumentException("acknowledgedBy is required", nameof(acknowledgedBy));
        }

        var tenantId = RequireTenantId();

        return _repository.BulkAcknowledgeAsync(
            tenantId, acknowledgedBy, contractId, alertType, cancellationToken);
    }

    public Task<DeadlineAlert?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public Task<PagedResult<DeadlineAlert>> ListAsync(
        AlertFilters filters,
        PageRequest request,
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(filters, request, cancellationToken);

    private Guid RequireTenantId()
    {
        if (!_tenantContext.IsResolved || _tenantContext.TenantId is null)
        {
            throw new UnauthorizedAccessException("API key required");
        }
        return _tenantContext.TenantId.Value;
    }
}
