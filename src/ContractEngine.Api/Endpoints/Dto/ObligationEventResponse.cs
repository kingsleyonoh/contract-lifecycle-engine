using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for a single row in the obligation event timeline. <c>from_status</c> and
/// <c>to_status</c> are stored as snake_case strings on the server (PRD §4.7) — we pass them
/// through unchanged. Returned as part of <see cref="ObligationDetailResponse"/>.
/// </summary>
public sealed class ObligationEventResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("obligation_id")]
    public Guid ObligationId { get; set; }

    [JsonPropertyName("from_status")]
    public string FromStatus { get; set; } = string.Empty;

    [JsonPropertyName("to_status")]
    public string ToStatus { get; set; } = string.Empty;

    [JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
