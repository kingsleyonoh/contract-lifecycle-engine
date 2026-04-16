using System.Text.Json.Serialization;

namespace ContractEngine.Api.Endpoints.Dto;

/// <summary>Wire shape for <c>GET /health</c> — minimal liveness probe.</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";
}

/// <summary>Wire shape for <c>GET /health/db</c> — DB round-trip probe with latency.</summary>
public sealed class HealthDbResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "healthy";

    [JsonPropertyName("latency_ms")]
    public long LatencyMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Wire shape for <c>GET /health/ready</c> — aggregate readiness probe.</summary>
public sealed class HealthReadyResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ready";

    [JsonPropertyName("integrations")]
    public IntegrationsReadiness Integrations { get; set; } = new();

    [JsonPropertyName("database")]
    public string Database { get; set; } = "healthy";
}

public sealed class IntegrationsReadiness
{
    [JsonPropertyName("rag")]
    public bool Rag { get; set; }

    [JsonPropertyName("hub")]
    public bool Hub { get; set; }

    [JsonPropertyName("nats")]
    public bool Nats { get; set; }

    [JsonPropertyName("webhook")]
    public bool Webhook { get; set; }

    [JsonPropertyName("workflow")]
    public bool Workflow { get; set; }

    [JsonPropertyName("invoice")]
    public bool Invoice { get; set; }
}
