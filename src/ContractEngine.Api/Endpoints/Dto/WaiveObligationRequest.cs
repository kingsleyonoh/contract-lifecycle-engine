using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Body for <c>POST /api/obligations/{id}/waive</c>. <see cref="Reason"/> is REQUIRED — waiving
/// an obligation without a documented rationale is a compliance liability, so the endpoint
/// rejects empty reasons with 400 VALIDATION_ERROR before the service runs.
/// </summary>
public sealed class WaiveObligationRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
