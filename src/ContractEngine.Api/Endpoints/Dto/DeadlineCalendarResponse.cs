using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire shape for <c>GET /api/analytics/deadline-calendar</c>.</summary>
public sealed class DeadlineCalendarResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<DeadlineCalendarItemDto> Data { get; set; } = Array.Empty<DeadlineCalendarItemDto>();
}

public sealed class DeadlineCalendarItemDto
{
    [JsonPropertyName("obligation_id")]
    public Guid ObligationId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("next_due_date")]
    public DateOnly NextDueDate { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("status")]
    public ObligationStatus Status { get; set; }
}
