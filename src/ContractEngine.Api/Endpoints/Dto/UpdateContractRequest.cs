using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>PATCH /api/contracts/{id}</c>. Every field is optional — absent fields leave
/// the stored value untouched (JSON PATCH convention). Status is intentionally NOT here; callers
/// transition status via the lifecycle endpoints (activate / terminate / archive) so state-machine
/// rules are enforced.
/// </summary>
public sealed class UpdateContractRequestWire
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("reference_number")]
    public string? ReferenceNumber { get; set; }

    [JsonPropertyName("contract_type")]
    public ContractType? ContractType { get; set; }

    [JsonPropertyName("counterparty_id")]
    public Guid? CounterpartyId { get; set; }

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

    // Accepted solely so we can reject it with a helpful 422 ("use lifecycle endpoints"). If the
    // value is absent (default null), no 422 is raised.
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
