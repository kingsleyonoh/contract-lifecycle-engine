using System.Text.Json.Serialization;

namespace ContractEngine.Api.Middleware;

/// <summary>
/// Wire-level shape of the error envelope defined in <c>CODEBASE_CONTEXT.md</c> Key Patterns §1
/// and PRD §8b. Every non-2xx response from the API serialises to this shape.
/// </summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public ErrorDetail Error { get; set; } = new();
}

public sealed class ErrorDetail
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "INTERNAL_ERROR";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "An unexpected error occurred";

    [JsonPropertyName("details")]
    public IReadOnlyList<ErrorFieldDetail> Details { get; set; } = Array.Empty<ErrorFieldDetail>();

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;
}

public sealed class ErrorFieldDetail
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
