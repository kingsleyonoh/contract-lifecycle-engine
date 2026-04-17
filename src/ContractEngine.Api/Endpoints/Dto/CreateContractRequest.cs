using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>POST /api/contracts</c>. Snake_case wire fields per the rest of the API.
/// Exactly one of <see cref="CounterpartyId"/> or <see cref="CounterpartyName"/> must be set —
/// when a name is supplied the server auto-creates the counterparty (PRD §5.1 edge case).
/// </summary>
public sealed class CreateContractRequestWire
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("reference_number")]
    public string? ReferenceNumber { get; set; }

    [JsonPropertyName("contract_type")]
    public ContractType ContractType { get; set; }

    [JsonPropertyName("counterparty_id")]
    public Guid? CounterpartyId { get; set; }

    [JsonPropertyName("counterparty_name")]
    public string? CounterpartyName { get; set; }

    [JsonPropertyName("effective_date")]
    public DateOnly? EffectiveDate { get; set; }

    [JsonPropertyName("end_date")]
    public DateOnly? EndDate { get; set; }

    [JsonPropertyName("renewal_notice_days")]
    public int? RenewalNoticeDays { get; set; }

    [JsonPropertyName("auto_renewal")]
    public bool? AutoRenewal { get; set; }

    [JsonPropertyName("auto_renewal_period_months")]
    public int? AutoRenewalPeriodMonths { get; set; }

    [JsonPropertyName("total_value")]
    public decimal? TotalValue { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("governing_law")]
    public string? GoverningLaw { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
