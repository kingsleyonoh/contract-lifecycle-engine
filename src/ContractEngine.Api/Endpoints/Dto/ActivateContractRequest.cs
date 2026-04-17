using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>POST /api/contracts/{id}/activate</c>. Both fields are optional — supply them
/// only when the caller wants to override values that were left blank on the original Draft. If
/// both are already set on the stored contract, the body can be empty (<c>{}</c>).
/// </summary>
public sealed class ActivateContractRequestWire
{
    [JsonPropertyName("effective_date")]
    public DateOnly? EffectiveDate { get; set; }

    [JsonPropertyName("end_date")]
    public DateOnly? EndDate { get; set; }
}
