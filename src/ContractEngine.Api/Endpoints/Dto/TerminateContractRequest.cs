using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>POST /api/contracts/{id}/terminate</c>. <see cref="Reason"/> is required —
/// stored in the contract's <c>metadata.termination_reason</c> so downstream reports can surface
/// the business rationale. <see cref="TerminationDate"/> is optional; when present it overrides
/// <c>Contract.EndDate</c> so reporting reflects the actual termination moment.
/// </summary>
public sealed class TerminateContractRequestWire
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("termination_date")]
    public DateOnly? TerminationDate { get; set; }
}
