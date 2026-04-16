using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire shape for <c>GET /api/analytics/contract-value</c>.</summary>
public sealed class ContractValueResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ContractValueItem> Data { get; set; } = Array.Empty<ContractValueItem>();
}

public sealed class ContractValueItem
{
    [JsonPropertyName("status")]
    public ContractStatus Status { get; set; }

    [JsonPropertyName("total_value")]
    public string TotalValue { get; set; } = "0.00";

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "USD";

    [JsonPropertyName("contract_count")]
    public int ContractCount { get; set; }

    [JsonPropertyName("counterparty_id")]
    public Guid? CounterpartyId { get; set; }
}
