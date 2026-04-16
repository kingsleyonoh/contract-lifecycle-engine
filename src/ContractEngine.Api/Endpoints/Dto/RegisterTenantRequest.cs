using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Request body for <c>POST /api/tenants/register</c>. Maps snake_case JSON fields (PRD §8b
/// example: <c>default_timezone</c>) to PascalCase properties via explicit attributes.
/// </summary>
public sealed class RegisterTenantRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("default_timezone")]
    public string? DefaultTimezone { get; set; }

    [JsonPropertyName("default_currency")]
    public string? DefaultCurrency { get; set; }
}
