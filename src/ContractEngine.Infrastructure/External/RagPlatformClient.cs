using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// Typed HTTP client for the Multi-Agent RAG Platform (PRD §5.6a). Constructed via
/// <see cref="IHttpClientFactory"/> so the per-request <c>HttpClient</c> is pooled and the
/// resilience pipeline (retry + circuit breaker) attached by
/// <c>Microsoft.Extensions.Http.Resilience</c> flows through automatically.
///
/// <para>Auth is a per-request <c>X-API-Key</c> header sourced from <c>RAG_PLATFORM_API_KEY</c>
/// config. Snake-case JSON on the wire matches the RAG Platform contract; request/response DTOs
/// are internal wire shapes that fan into the Core-layer records (<see cref="RagDocument"/>,
/// <see cref="RagSearchResult"/>, etc.).</para>
///
/// <para>Non-success HTTP responses surface as <see cref="RagPlatformException"/> with the upstream
/// status code and (best-effort) body attached. Transport-level failures (DNS, TLS, connection
/// refused) also funnel into <see cref="RagPlatformException"/> after the resilience handler
/// exhausts its retry budget.</para>
/// </summary>
public sealed class RagPlatformClient : IRagPlatformClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RagPlatformClient> _logger;
    private readonly string? _apiKey;

    public RagPlatformClient(
        HttpClient httpClient,
        ILogger<RagPlatformClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["RAG_PLATFORM_API_KEY"];
    }

    public async Task<RagDocument> UploadDocumentAsync(
        Stream fileContent,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileContent);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(streamContent, "file", fileName);

        using var request = BuildRequest(HttpMethod.Post, "/api/documents");
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "upload", cancellationToken).ConfigureAwait(false);

        var wire = await response.Content
            .ReadFromJsonAsync<UploadWire>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RagPlatformException("RAG Platform returned an empty upload response.");

        return new RagDocument(wire.Id ?? string.Empty, wire.FileName ?? fileName, wire.Status ?? "unknown");
    }

    public async Task<RagSearchResult> SearchAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        CancellationToken cancellationToken = default)
    {
        var body = new { query, filters = filters ?? new Dictionary<string, object>() };

        using var request = BuildRequest(HttpMethod.Post, "/api/search");
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "search", cancellationToken).ConfigureAwait(false);

        var wire = await response.Content
            .ReadFromJsonAsync<SearchWire>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var hits = wire?.Hits?
            .Select(h => new RagSearchHit(h.DocumentId ?? string.Empty, h.Chunk ?? string.Empty, h.Score))
            .ToList() ?? new List<RagSearchHit>();

        return new RagSearchResult(hits);
    }

    public async Task<RagChatResult> ChatSyncAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        string? responseFormat,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            query,
            filters = filters ?? new Dictionary<string, object>(),
            response_format = responseFormat,
        };

        using var request = BuildRequest(HttpMethod.Post, "/api/chat/sync");
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "chat.sync", cancellationToken).ConfigureAwait(false);

        var wire = await response.Content
            .ReadFromJsonAsync<ChatWire>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RagPlatformException("RAG Platform returned an empty chat response.");

        var sources = wire.Sources?
            .Select(s => new RagChatSource(s.DocumentId ?? string.Empty, s.Chunk ?? string.Empty))
            .ToList() ?? new List<RagChatSource>();

        return new RagChatResult(wire.Answer ?? string.Empty, sources);
    }

    public async Task<IReadOnlyList<RagEntity>> GetEntitiesAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        var path = $"/api/entities?document_id={Uri.EscapeDataString(documentId)}";
        using var request = BuildRequest(HttpMethod.Get, path);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "entities", cancellationToken).ConfigureAwait(false);

        var wire = await response.Content
            .ReadFromJsonAsync<EntitiesWire>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return wire?.Entities?
            .Select(e => new RagEntity(e.Type ?? string.Empty, e.Value ?? string.Empty, e.Confidence))
            .ToList()
            ?? (IReadOnlyList<RagEntity>)Array.Empty<RagEntity>();
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("X-API-Key", _apiKey);
        }
        return request;
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort diagnostics only — swallow so we always raise the upstream-status error.
        }

        _logger.LogWarning(
            "RAG Platform {Operation} failed with {StatusCode}",
            operation,
            (int)response.StatusCode);

        throw new RagPlatformException(
            $"RAG Platform {operation} failed with HTTP {(int)response.StatusCode}.",
            statusCode: (int)response.StatusCode,
            responseBody: body);
    }

    // -------- wire DTOs (private — these match the RAG Platform JSON contract exactly) --------

    private sealed record UploadWire(string? Id, string? FileName, string? Status);

    private sealed record SearchWire(List<SearchHitWire>? Hits);
    private sealed record SearchHitWire(string? DocumentId, string? Chunk, double Score);

    private sealed record ChatWire(string? Answer, List<ChatSourceWire>? Sources);
    private sealed record ChatSourceWire(string? DocumentId, string? Chunk);

    private sealed record EntitiesWire(List<EntityWire>? Entities);
    private sealed record EntityWire(string? Type, string? Value, double Confidence);
}
