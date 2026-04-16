using System.Text.Json.Serialization;
using ContractEngine.Core.Enums;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Wire shape for a single <c>deadline_alerts</c> row. Flat projection of
/// <see cref="ContractEngine.Core.Models.DeadlineAlert"/>. Enums serialise as snake_case lowercase
/// via the global <c>JsonStringEnumConverter(SnakeCaseLower)</c> in <c>Program.cs</c>.
/// </summary>
public sealed class AlertResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("obligation_id")]
    public Guid ObligationId { get; set; }

    [JsonPropertyName("contract_id")]
    public Guid ContractId { get; set; }

    [JsonPropertyName("alert_type")]
    public AlertType AlertType { get; set; }

    [JsonPropertyName("days_remaining")]
    public int? DaysRemaining { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }

    [JsonPropertyName("acknowledged_at")]
    public DateTime? AcknowledgedAt { get; set; }

    [JsonPropertyName("acknowledged_by")]
    public string? AcknowledgedBy { get; set; }

    [JsonPropertyName("notification_sent")]
    public bool NotificationSent { get; set; }

    [JsonPropertyName("notification_sent_at")]
    public DateTime? NotificationSentAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}
