using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// ObligationEvent — an immutable row in the event-sourced status history for an
/// <see cref="Obligation"/>. PRD §4.7 requires INSERT-only semantics: no UPDATE, no DELETE; the
/// repository surface reflects this (add + list only — <c>IObligationEventRepository</c>). The
/// table has no updated_at column on purpose.
///
/// <para><see cref="FromStatus"/> and <see cref="ToStatus"/> are stored as snake_case lowercase
/// strings (not typed enums) because obligation-level status codes are a closed wire-format set —
/// storing strings decouples the event log from future enum rearrangements and keeps the data
/// audit-safe even if the in-memory enum values shift.</para>
///
/// <para><see cref="Actor"/> is an opaque identifier describing who performed the transition:
/// <c>"system"</c>, <c>"user:&lt;email&gt;"</c>, or <c>"scheduler:deadline_scanner"</c>
/// (matches PRD §4.7).</para>
///
/// <para>Tenant isolation: implements <see cref="ITenantScoped"/>.</para>
/// </summary>
public class ObligationEvent : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ObligationId { get; set; }

    public string FromStatus { get; set; } = string.Empty;

    public string ToStatus { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>Free-form JSONB metadata. Stored as JSON string via EF value converter.</summary>
    public Dictionary<string, object>? Metadata { get; set; }

    public DateTime CreatedAt { get; set; }
}
