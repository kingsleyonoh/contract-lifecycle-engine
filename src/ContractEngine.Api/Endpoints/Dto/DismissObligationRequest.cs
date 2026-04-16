using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Optional body for <c>POST /api/obligations/{id}/dismiss</c>. The only field is
/// <see cref="Reason"/>, captured verbatim on the resulting <c>obligation_events</c> row so the
/// audit log explains WHY the obligation was dismissed. An empty body / missing reason is
/// allowed — dismissal is still a terminal transition regardless of whether a reason was given.
/// </summary>
public sealed class DismissObligationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
