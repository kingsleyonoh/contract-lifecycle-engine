using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Body for <c>POST /api/obligations/{id}/dispute</c>. <see cref="Reason"/> is REQUIRED so the
/// event log captures why the obligation was contested. The endpoint validates non-empty before
/// hitting the service.
/// </summary>
public sealed class DisputeObligationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
