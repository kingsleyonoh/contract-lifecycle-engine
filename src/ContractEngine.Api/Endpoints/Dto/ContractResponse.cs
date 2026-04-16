using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape returned by every contract endpoint. <c>obligations_count</c> is stubbed to 0 and
/// <c>latest_version</c> mirrors <c>current_version</c> until the Obligations and ContractVersions
/// modules ship (Batches 008+). Both fields are present today so SDK clients generated from the
/// OpenAPI spec don't break on later schema drift.
/// </summary>
public sealed class ContractResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("counterparty_id")]
    public Guid CounterpartyId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("reference_number")]
    public string? ReferenceNumber { get; set; }

    [JsonPropertyName("contract_type")]
    public ContractType ContractType { get; set; }

    [JsonPropertyName("status")]
    public ContractStatus Status { get; set; }

    [JsonPropertyName("effective_date")]
    public DateOnly? EffectiveDate { get; set; }

    [JsonPropertyName("end_date")]
    public DateOnly? EndDate { get; set; }

    [JsonPropertyName("renewal_notice_days")]
    public int RenewalNoticeDays { get; set; }

    [JsonPropertyName("auto_renewal")]
    public bool AutoRenewal { get; set; }

    [JsonPropertyName("auto_renewal_period_months")]
    public int? AutoRenewalPeriodMonths { get; set; }

    [JsonPropertyName("total_value")]
    public decimal? TotalValue { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("governing_law")]
    public string? GoverningLaw { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("rag_document_id")]
    public string? RagDocumentId { get; set; }

    [JsonPropertyName("current_version")]
    public int CurrentVersion { get; set; }

    [JsonPropertyName("obligations_count")]
    public int ObligationsCount { get; set; }

    [JsonPropertyName("latest_version")]
    public int LatestVersion { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
