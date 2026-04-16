using ContractEngine.Core.Integrations.Rag;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the Multi-Agent RAG Platform (PRD §5.6a). The real implementation
/// (<c>RagPlatformClient</c>, Infrastructure layer) is a typed HTTP client with retries and a
/// circuit breaker; the no-op stub (<c>NoOpRagPlatformClient</c>) is registered when
/// <c>RAG_PLATFORM_ENABLED=false</c>.
///
/// <para>Return-shape policy for the no-op: reads (<see cref="SearchAsync"/>,
/// <see cref="GetEntitiesAsync"/>) yield empty collections so downstream pipelines keep flowing;
/// writes (<see cref="UploadDocumentAsync"/>, <see cref="ChatSyncAsync"/>) throw so data loss /
/// silently-skipped extractions surface as errors.</para>
/// </summary>
public interface IRagPlatformClient
{
    /// <summary>
    /// Uploads a document to the RAG Platform via <c>POST /api/documents</c> (multipart/form-data).
    /// The returned <see cref="RagDocument.Id"/> is the handle callers persist for future queries.
    /// </summary>
    Task<RagDocument> UploadDocumentAsync(
        Stream fileContent,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Semantic search via <c>POST /api/search</c>. <paramref name="filters"/> (e.g.
    /// <c>{ "document_id": "..." }</c>) is forwarded verbatim to the RAG Platform; null means no
    /// filter.
    /// </summary>
    Task<RagSearchResult> SearchAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous LLM chat via <c>POST /api/chat/sync</c>. <paramref name="responseFormat"/> may
    /// be <c>null</c> (plain text), <c>"json"</c> (free-form JSON object), or a JSON-schema string
    /// — forwarded verbatim to the RAG Platform.
    /// </summary>
    Task<RagChatResult> ChatSyncAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        string? responseFormat,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Named-entity retrieval via <c>GET /api/entities?document_id=...</c>. Returns the empty
    /// list when no entities are available.
    /// </summary>
    Task<IReadOnlyList<RagEntity>> GetEntitiesAsync(
        string documentId,
        CancellationToken cancellationToken = default);
}
