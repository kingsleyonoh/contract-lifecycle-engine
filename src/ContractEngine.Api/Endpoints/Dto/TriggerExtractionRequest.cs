using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire DTO for <c>POST /api/contracts/{id}/extract</c>.</summary>
public sealed class TriggerExtractionRequest
{
    [JsonPropertyName("prompt_types")]
    public string[]? PromptTypes { get; set; }

    [JsonPropertyName("document_id")]
    public Guid? DocumentId { get; set; }
}
