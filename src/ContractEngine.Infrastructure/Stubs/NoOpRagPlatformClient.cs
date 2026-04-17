using ContractEngine.Core.Integrations.Rag;
using ContractEngine.Core.Interfaces;

namespace ContractEngine.Infrastructure.Stubs;

/// <summary>
/// No-op <see cref="IRagPlatformClient"/> registered when <c>RAG_PLATFORM_ENABLED=false</c>.
///
/// <para>Read paths return empty results so downstream pipelines (analytics, UI lists) can render
/// cleanly without special-casing "RAG disabled". Write paths (<see cref="UploadDocumentAsync"/>,
/// <see cref="ChatSyncAsync"/>) throw — silently dropping an upload would lose data, and silently
/// skipping an extraction chat call would mark a job as successful when in fact it produced
/// zero obligations. Failing loudly is the safer default.</para>
/// </summary>
public sealed class NoOpRagPlatformClient : IRagPlatformClient
{
    private const string DisabledMessage = "RAG Platform is disabled (RAG_PLATFORM_ENABLED=false)";

    public Task<RagDocument> UploadDocumentAsync(
        Stream fileContent,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(DisabledMessage);

    public Task<RagSearchResult> SearchAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RagSearchResult(Array.Empty<RagSearchHit>()));

    public Task<RagChatResult> ChatSyncAsync(
        string query,
        IReadOnlyDictionary<string, object>? filters,
        string? responseFormat,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(DisabledMessage);

    public Task<IReadOnlyList<RagEntity>> GetEntitiesAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RagEntity>>(Array.Empty<RagEntity>());
}
