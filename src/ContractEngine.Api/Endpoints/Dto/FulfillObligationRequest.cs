using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Optional body for <c>POST /api/obligations/{id}/fulfill</c>. <see cref="Notes"/> is
/// free-form text appended to the resulting event's reason so operators can capture the
/// fulfilment rationale (e.g. "paid via ACH ref 12345", "evidence attached in ticket 4321").
/// An empty body is allowed — fulfilment is still legal without a note.
/// </summary>
public sealed class FulfillObligationRequest
{
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
