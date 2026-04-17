using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Response shape for <c>GET /api/obligations/{id}</c>. Extends <see cref="ObligationResponse"/>
/// with the full chronological event timeline inlined (ascending by <c>created_at</c>), so a
/// typical UI render is one REST round-trip.
///
/// <para>Uses composition rather than inheritance so the shared <c>JsonStringEnumConverter</c>
/// keeps flat serialisation; the <see cref="Events"/> collection serialises alongside the
/// obligation fields at the top level of the response object via a property spread.</para>
/// </summary>
public sealed class ObligationDetailResponse
{
    // Obligation fields — mirrors ObligationResponse. Duplicated rather than inherited so the
    // JSON serialiser emits a flat object without requiring converter gymnastics.
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("obligation_type")]
    public Core.Enums.ObligationType ObligationType { get; set; }

    [JsonPropertyName("status")]
    public Core.Enums.ObligationStatus Status { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("responsible_party")]
    public Core.Enums.ResponsibleParty ResponsibleParty { get; set; }

    [JsonPropertyName("deadline_date")]
    public DateOnly? DeadlineDate { get; set; }

    [JsonPropertyName("deadline_formula")]
    public string? DeadlineFormula { get; set; }

    [JsonPropertyName("recurrence")]
    public Core.Enums.ObligationRecurrence? Recurrence { get; set; }

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
    public Core.Enums.ObligationSource Source { get; set; }

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

    [JsonPropertyName("events")]
    public IReadOnlyList<ObligationEventResponse> Events { get; set; } = Array.Empty<ObligationEventResponse>();
}
