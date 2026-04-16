using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire shape for <c>GET /api/analytics/obligations-by-type</c>.</summary>
public sealed class ObligationsByTypeResponse
{
    [JsonPropertyName("period")]
    public string Period { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public IReadOnlyList<ObligationsByTypeItem> Data { get; set; } = Array.Empty<ObligationsByTypeItem>();
}

public sealed class ObligationsByTypeItem
{
    [JsonPropertyName("type")]
    public ObligationType Type { get; set; }

    [JsonPropertyName("status")]
    public ObligationStatus Status { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
