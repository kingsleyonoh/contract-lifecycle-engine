using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Body for <c>POST /api/obligations/{id}/resolve-dispute</c>. <see cref="Resolution"/> is
/// REQUIRED and must be <c>"stands"</c> (Disputed → Active) or <c>"waived"</c> (Disputed →
/// Waived) — any other value is rejected with 400 VALIDATION_ERROR at the endpoint. Case-
/// insensitive parsing; accepts <c>"stands"</c>, <c>"Stands"</c>, <c>"STANDS"</c>, etc.
///
/// <para><see cref="Notes"/> is optional; when present it's appended to the event reason for
/// audit traceability.</para>
/// </summary>
public sealed class ResolveDisputeRequest
{
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
