using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Interfaces;
using ContractEngine.Core.Models;
using ContractEngine.Core.Pagination;

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

    public DeadlineAlertService(
        IDeadlineAlertRepository repository,
        ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
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
        return alert;
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
