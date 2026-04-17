using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape returned by every contract document endpoint. Matches the snake_case envelope
/// convention used by the rest of the API. <c>rag_document_id</c> is populated only after a
/// document has been ingested by the RAG Platform (Phase 2) — today it's always null.
/// </summary>
public sealed class ContractDocumentResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("version_number")]
    public int? VersionNumber { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("file_size_bytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("rag_document_id")]
    public string? RagDocumentId { get; set; }

    [JsonPropertyName("uploaded_at")]
    public DateTime UploadedAt { get; set; }

    [JsonPropertyName("uploaded_by")]
    public string? UploadedBy { get; set; }
}
