using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire DTO for extraction job list items and trigger response.</summary>
public sealed class ExtractionJobResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("document_id")]
    public Guid? DocumentId { get; set; }

    [JsonPropertyName("status")]
    public ExtractionStatus Status { get; set; }

    [JsonPropertyName("prompt_types")]
    public string[] PromptTypes { get; set; } = Array.Empty<string>();

    [JsonPropertyName("obligations_found")]
    public int ObligationsFound { get; set; }

    [JsonPropertyName("obligations_confirmed")]
    public int ObligationsConfirmed { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("rag_document_id")]
    public string? RagDocumentId { get; set; }

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
