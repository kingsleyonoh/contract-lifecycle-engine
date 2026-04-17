using System.Text.Json;
using ContractEngine.Core.Integrations.Notifications;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the Event-Driven Notification Hub (PRD §5.6b). Constructed via
/// <see cref="IHttpClientFactory"/> so the per-request <see cref="HttpClient"/> is pooled and the
/// resilience pipeline (retry + circuit breaker) attached by
/// <c>Microsoft.Extensions.Http.Resilience</c> flows through automatically.
///
/// <para>Auth is a per-request <c>X-API-Key</c> header sourced from <c>NOTIFICATION_HUB_API_KEY</c>
/// config. Snake-case JSON on the wire matches the Hub's <c>POST /api/events</c> contract. The body
/// is <c>{ event_type: ..., payload: {...} }</c>; the Hub's template engine reads both fields.</para>
///
/// <para>Non-success HTTP responses surface as <see cref="NotificationHubException"/> with the
/// upstream status code and (best-effort) body attached. Call sites in the domain services catch
/// this exception and log it — notification dispatch is fire-and-forget and MUST NOT roll back the
/// domain transaction that produced the event.</para>
/// </summary>
public sealed class NotificationHubClient : INotificationPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<NotificationHubClient> _logger;
    private readonly string? _apiKey;

    public NotificationHubClient(
        HttpClient httpClient,
        ILogger<NotificationHubClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["NOTIFICATION_HUB_API_KEY"];
    }

    public async Task<NotificationDispatchResult> PublishEventAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("event_type is required", nameof(eventType));
        }

        var body = new { event_type = eventType, payload };
        var json = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
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
                "Notification Hub publish of {EventType} failed with {StatusCode}",
                eventType,
                (int)response.StatusCode);

            throw new NotificationHubException(
                $"Notification Hub publish of {eventType} failed with HTTP {(int)response.StatusCode}.",
                statusCode: (int)response.StatusCode,
                responseBody: respBody);
        }

        // Parse response body for event_id echo — Hub MAY return it, MAY omit it.
        string? eventId = null;
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var wire = JsonSerializer.Deserialize<AckWire>(raw, JsonOptions);
                eventId = wire?.EventId;
            }
        }
        catch
        {
            // Missing / malformed body is OK — dispatch succeeded on status alone.
        }

        return new NotificationDispatchResult(Dispatched: true, EventId: eventId);
    }

    private sealed record AckWire(string? EventId, string? Status);
}
