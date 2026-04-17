using System.Text.Json;
using ContractEngine.Core.Integrations.Workflow;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the Workflow Automation Engine (PRD §5.6d). Constructed via
/// <see cref="IHttpClientFactory"/> so the per-request <see cref="HttpClient"/> is pooled and the
/// resilience pipeline (retry + circuit breaker) attached by
/// <c>Microsoft.Extensions.Http.Resilience</c> flows through automatically.
///
/// <para>Auth is a per-request <c>X-API-Key</c> header sourced from <c>WORKFLOW_ENGINE_API_KEY</c>
/// config. Snake-case JSON on the wire matches the engine's <c>POST /webhooks/{path}</c> contract.
/// Non-success HTTP responses surface as <see cref="WorkflowEngineException"/>; call sites catch
/// and log — workflow triggering is best-effort.</para>
/// </summary>
public sealed class WorkflowEngineClient : IWorkflowTrigger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<WorkflowEngineClient> _logger;
    private readonly string? _apiKey;

    public WorkflowEngineClient(
        HttpClient httpClient,
        ILogger<WorkflowEngineClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["WORKFLOW_ENGINE_API_KEY"];
    }

    public async Task<WorkflowTriggerResult> TriggerWorkflowAsync(
        string webhookPath,
        object payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookPath))
        {
            throw new ArgumentException("webhook_path is required", nameof(webhookPath));
        }

        // Strip any leading slash callers might accidentally include.
        var path = webhookPath.TrimStart('/');
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{path}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("X-API-Key", _apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? respBody = null;
            try
            {
                respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            _logger.LogWarning(
                "Workflow Engine trigger of {WebhookPath} failed with {StatusCode}",
                path,
                (int)response.StatusCode);

            throw new WorkflowEngineException(
                $"Workflow Engine trigger of {path} failed with HTTP {(int)response.StatusCode}.",
                statusCode: (int)response.StatusCode,
                responseBody: respBody);
        }

        // Parse response body for instance_id echo — engine MAY return it, MAY omit it.
        string? instanceId = null;
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var wire = JsonSerializer.Deserialize<AckWire>(raw, JsonOptions);
                instanceId = wire?.InstanceId;
            }
        }
        catch
        {
            // Missing / malformed body is OK — trigger succeeded on status alone.
        }

        return new WorkflowTriggerResult(Triggered: true, InstanceId: instanceId);
    }

    private sealed record AckWire(string? InstanceId, string? Status);
}
