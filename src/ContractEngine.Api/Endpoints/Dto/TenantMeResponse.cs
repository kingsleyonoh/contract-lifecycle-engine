using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Response body for <c>GET /api/tenants/me</c> and <c>PATCH /api/tenants/me</c>. Fields use
/// snake_case JSON to match the error envelope and the rest of the public API surface.
/// </summary>
public sealed class TenantMeResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("default_timezone")]
    public string DefaultTimezone { get; set; } = "UTC";

    [JsonPropertyName("default_currency")]
    public string DefaultCurrency { get; set; } = "USD";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
