using ContractEngine.Core.Enums;

namespace ContractEngine.Core.Services;

/// <summary>
/// Metadata keys reserved by the engine itself — callers of the public API MUST NOT set these.
/// Batch 026 security-audit finding F: the webhook ingestion pipeline stamps contracts with these
/// keys for idempotency + provenance (<c>webhook_envelope_id</c>, <c>webhook_source</c>, etc.), so
/// a caller spoofing one of them on <c>POST /api/contracts</c> or <c>PATCH /api/contracts/{id}</c>
/// could collide with a future webhook redelivery and silently hijack its idempotency key. The
/// validators on public request DTOs (<see cref="CreateContractRequest"/>,
/// <see cref="UpdateContractRequest"/>) reject these keys; the webhook helpers set them directly
/// without passing through the validator, which is safe because they run post-HMAC and write the
/// canonical provenance themselves.
/// </summary>
public static class ContractMetadataReservedKeys
{
    /// <summary>DocuSign envelope identifier (idempotency probe for DocuSign payloads).</summary>
    public const string WebhookEnvelopeId = "webhook_envelope_id";

    /// <summary>PandaDoc document identifier (idempotency probe for PandaDoc payloads).</summary>
    public const string WebhookDocumentId = "webhook_document_id";

    /// <summary>The webhook source ("docusign" / "pandadoc") — set by the handler.</summary>
    public const string WebhookSource = "webhook_source";

    /// <summary>UTC timestamp when the webhook was received (ISO 8601, set by the handler).</summary>
    public const string WebhookReceivedAt = "webhook_received_at";

    /// <summary>UTC timestamp the payload declared as "signed / completed" (ISO 8601).</summary>
    public const string SignedCompletedAt = "signed_completed_at";

    /// <summary>
    /// Canonical set of reserved keys. Enumerated by <c>CreateContractRequestValidator</c> and
    /// <c>UpdateContractRequestValidator</c> — adding a new reserved key here automatically
    /// extends validator coverage without changing the validator code.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WebhookEnvelopeId,
        WebhookDocumentId,
        WebhookSource,
        WebhookReceivedAt,
        SignedCompletedAt,
    };
}

/// <summary>
/// Request envelope for <see cref="ContractService.CreateAsync"/>. Either
/// <see cref="CounterpartyId"/> or <see cref="CounterpartyName"/> must be supplied — never both.
/// Extracted from <c>ContractService.cs</c> (Batch 026 modularity gate) so the service file stays
/// under the 300-line cap and request shapes live beside each other.
/// </summary>
public sealed record CreateContractRequest
{
    public string Title { get; init; } = string.Empty;
    public string? ReferenceNumber { get; init; }
    public ContractType ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public string? CounterpartyName { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Partial-update request for <see cref="ContractService.UpdateAsync"/>. Every field is optional;
/// absent fields leave the stored value untouched. <see cref="ContractStatus"/> is NOT here — use
/// the lifecycle methods for transitions.
/// </summary>
public sealed record UpdateContractRequest
{
    public string? Title { get; init; }
    public string? ReferenceNumber { get; init; }
    public ContractType? ContractType { get; init; }
    public Guid? CounterpartyId { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? RenewalNoticeDays { get; init; }
    public bool? AutoRenewal { get; init; }
    public int? AutoRenewalPeriodMonths { get; init; }
    public decimal? TotalValue { get; init; }
    public string? Currency { get; init; }
    public string? GoverningLaw { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
