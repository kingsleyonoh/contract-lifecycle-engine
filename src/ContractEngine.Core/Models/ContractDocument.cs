using ContractEngine.Core.Abstractions;
using ContractEngine.Core.Pagination;

namespace ContractEngine.Core.Models;

/// <summary>
/// ContractDocument — an uploaded contract file (PDF / DOCX / TXT) attached to a
/// <see cref="Contract"/>. PRD §4.5 defines the schema. Storage layout on disk follows
/// <c>{DOCUMENT_STORAGE_PATH}/{tenant_id}/{contract_id}/{filename}</c>; the row records the
/// filesystem path via <see cref="FilePath"/> (relative to the storage root) so the engine can
/// resolve documents regardless of where the root moves between environments.
///
/// Tenant isolation: implements <see cref="ITenantScoped"/>, so reads are filtered by
/// <c>ContractDbContext</c>'s global query filter. Pagination implements <see cref="IHasCursor"/>
/// for the shared <c>(CreatedAt, Id)</c> cursor — the <c>uploaded_at</c> column backs the
/// <see cref="CreatedAt"/> CLR property so the shared cursor extension composes cleanly. Upload
/// time is exposed to the wire as <c>uploaded_at</c>; the CLR name is a project convention that
/// keeps every entity uniform.
/// </summary>
public class ContractDocument : ITenantScoped, IHasCursor
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ContractId { get; set; }

    /// <summary>Which contract version this doc belongs to. <c>null</c> = original/primary doc.</summary>
    public int? VersionNumber { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Relative path under the storage root — e.g. <c>{tenant_id}/{contract_id}/{filename}</c>.</summary>
    public string FilePath { get; set; } = string.Empty;

    public long? FileSizeBytes { get; set; }

    public string? MimeType { get; set; }

    /// <summary>Populated after the document has been uploaded to the RAG Platform (Phase 2).</summary>
    public string? RagDocumentId { get; set; }

    /// <summary>CLR <c>CreatedAt</c> → DB <c>uploaded_at</c>. Upload time of the file.</summary>
    public DateTime CreatedAt { get; set; }

    public string? UploadedBy { get; set; }
}
