using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for <c>GET /api/analytics/dashboard</c>. Flat projection — no pagination envelope
/// because a dashboard is a single aggregated snapshot, not a list.
/// </summary>
public sealed class DashboardResponse
{
    [JsonPropertyName("active_contracts")]
    public int ActiveContracts { get; set; }

    [JsonPropertyName("pending_obligations")]
    public int PendingObligations { get; set; }

    [JsonPropertyName("overdue_count")]
    public int OverdueCount { get; set; }

    [JsonPropertyName("upcoming_deadlines_7d")]
    public int UpcomingDeadlines7d { get; set; }

    [JsonPropertyName("upcoming_deadlines_30d")]
    public int UpcomingDeadlines30d { get; set; }

    [JsonPropertyName("expiring_contracts_90d")]
    public int ExpiringContracts90d { get; set; }

    [JsonPropertyName("unacknowledged_alerts")]
    public int UnacknowledgedAlerts { get; set; }
}
