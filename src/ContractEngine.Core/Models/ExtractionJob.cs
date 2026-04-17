using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Enums;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// ExtractionJob entity — tracks an AI extraction run against a contract document. PRD §4.8
/// defines the schema. The job goes through the lifecycle: queued → processing → completed |
/// partial | failed, with retry support (failed/partial → queued).
///
/// <para>Tenant isolation: implements <see cref="ITenantScoped"/>. Pagination: implements
/// <see cref="IHasCursor"/> so list endpoints use the shared <c>(CreatedAt, Id)</c> cursor
/// helper.</para>
///
/// <para><c>PromptTypes</c> is stored as <c>TEXT[]</c> in Postgres — Npgsql maps <c>string[]</c>
/// natively. <c>RawResponses</c> is stored as <c>JSONB</c>.</para>
/// </summary>
public class ExtractionJob : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    /// <summary>
    /// Optional FK to <c>contract_documents</c> — the specific document being extracted. Null
    /// when extraction is contract-level (e.g. the latest uploaded document is auto-selected).
    /// </summary>
    public Guid? DocumentId { get; set; }

    public ExtractionStatus Status { get; set; } = ExtractionStatus.Queued;

    /// <summary>
    /// Which extraction prompts to run: e.g. <c>["payment", "renewal", "compliance", "performance"]</c>.
    /// Stored as <c>TEXT[]</c> in PostgreSQL (Npgsql maps <c>string[]</c> natively).
    /// </summary>
    public string[] PromptTypes { get; set; } = Array.Empty<string>();

    public int ObligationsFound { get; set; }

    public int ObligationsConfirmed { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>RAG Platform document ID used for extraction — stored after upload.</summary>
    public string? RagDocumentId { get; set; }

    /// <summary>
    /// Raw RAG Platform responses for debugging — JSONB. Keyed by prompt_type.
    /// </summary>
    public Dictionary<string, object>? RawResponses { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public int RetryCount { get; set; }
}
