using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape returned by every obligation endpoint. Flat projection of
/// <see cref="ContractEngine.Core.Models.Obligation"/>. Enums serialise as snake_case lowercase
/// via the global <c>JsonStringEnumConverter(SnakeCaseLower)</c> in <c>Program.cs</c>.
/// </summary>
public sealed class ObligationResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("obligation_type")]
    public ObligationType ObligationType { get; set; }

    [JsonPropertyName("status")]
    public ObligationStatus Status { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("responsible_party")]
    public ResponsibleParty ResponsibleParty { get; set; }

    [JsonPropertyName("deadline_date")]
    public DateOnly? DeadlineDate { get; set; }

    [JsonPropertyName("deadline_formula")]
    public string? DeadlineFormula { get; set; }

    [JsonPropertyName("recurrence")]
    public ObligationRecurrence? Recurrence { get; set; }

    [JsonPropertyName("next_due_date")]
    public DateOnly? NextDueDate { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("alert_window_days")]
    public int AlertWindowDays { get; set; }

    [JsonPropertyName("grace_period_days")]
    public int GracePeriodDays { get; set; }

    [JsonPropertyName("business_day_calendar")]
    public string BusinessDayCalendar { get; set; } = "US";

    [JsonPropertyName("source")]
    public ObligationSource Source { get; set; }

    [JsonPropertyName("extraction_job_id")]
    public Guid? ExtractionJobId { get; set; }

    [JsonPropertyName("confidence_score")]
    public decimal? ConfidenceScore { get; set; }

    [JsonPropertyName("clause_reference")]
    public string? ClauseReference { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
