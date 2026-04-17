using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>PATCH /api/counterparties/{id}</c>. Every field is optional — absent fields
/// leave the stored value untouched (JSON PATCH convention used across the API).
/// </summary>
public sealed class UpdateCounterpartyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("legal_name")]
    public string? LegalName { get; set; }

    [JsonPropertyName("industry")]
    public string? Industry { get; set; }

    [JsonPropertyName("contact_email")]
    public string? ContactEmail { get; set; }

    [JsonPropertyName("contact_name")]
    public string? ContactName { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
