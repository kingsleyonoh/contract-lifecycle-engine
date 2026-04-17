using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// JSON body for <c>POST /api/obligations</c>. Snake_case wire fields per the rest of the API;
/// validated by <c>CreateObligationRequestValidator</c> (Core) after the endpoint maps into
/// <see cref="ContractEngine.Core.Validation.CreateObligationRequestDomain"/>.
///
/// <para>Either <see cref="DeadlineDate"/>, <see cref="DeadlineFormula"/>, or
/// <see cref="Recurrence"/> must be supplied so the obligation has a computable schedule —
/// enforced at the validator, not here. All other fields are optional with documented defaults
/// applied by the service layer.</para>
///
/// <para>Named with the <c>Wire</c> suffix to disambiguate from the Core-layer domain record
/// (<c>ContractEngine.Core.Services.CreateObligationRequest</c>) that the service consumes.
/// Mirrors the <c>CreateContractRequestWire</c> / <c>CreateContractRequest</c> split established
/// in Batch 007.</para>
/// </summary>
public sealed class CreateObligationRequestWire
{
    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("obligation_type")]
    public ObligationType ObligationType { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("responsible_party")]
    public string? ResponsibleParty { get; set; }

    [JsonPropertyName("deadline_date")]
    public DateOnly? DeadlineDate { get; set; }

    [JsonPropertyName("deadline_formula")]
    public string? DeadlineFormula { get; set; }

    [JsonPropertyName("recurrence")]
    public ObligationRecurrence? Recurrence { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("alert_window_days")]
    public int? AlertWindowDays { get; set; }

    [JsonPropertyName("grace_period_days")]
    public int? GracePeriodDays { get; set; }

    [JsonPropertyName("business_day_calendar")]
    public string? BusinessDayCalendar { get; set; }

    [JsonPropertyName("clause_reference")]
    public string? ClauseReference { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
}
