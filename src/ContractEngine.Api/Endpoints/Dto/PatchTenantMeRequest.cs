using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Request body for <c>PATCH /api/tenants/me</c> — all fields optional / nullable so callers can
/// send a partial update. Fields left null (absent from the JSON) are not touched; non-null
/// values overwrite the tenant row after validation.
/// </summary>
public sealed class PatchTenantMeRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("default_timezone")]
    public string? DefaultTimezone { get; set; }

    [JsonPropertyName("default_currency")]
    public string? DefaultCurrency { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
