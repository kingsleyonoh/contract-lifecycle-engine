using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>
/// Optional body for <c>POST /api/alerts/acknowledge-all</c>. Both fields are optional — an empty
/// body (or a literal <c>{}</c>) acknowledges every unacked alert for the tenant. Supplying
/// <see cref="ContractId"/> narrows to that contract; supplying <see cref="AlertType"/> narrows to
/// that alert type. Both combine with AND semantics.
/// </summary>
public sealed class AcknowledgeAllRequest
{
    [JsonPropertyName("contract_id")]
    public Guid? ContractId { get; set; }

    /// <summary>
    /// Accepts the snake_case wire form (<c>"deadline_approaching"</c>). Endpoint parses to the
    /// enum before calling the service; unknown values produce a 400 VALIDATION_ERROR.
    /// </summary>
    [JsonPropertyName("alert_type")]
    public string? AlertType { get; set; }
}
