namespace ContractEngine.Core.Integrations.Rag;

/// <summary>
/// Paginated set of semantic-search hits from <c>POST /api/search</c>. <see cref="Hits"/> is
/// always non-null (empty on no match) so callers can iterate without a guard.
/// </summary>
public sealed record RagSearchResult(IReadOnlyList<RagSearchHit> Hits);

/// <summary>
/// A single chunk-level hit. <see cref="Score"/> is the RAG Platform's relevance score in
/// [0, 1] — higher is more relevant.
/// </summary>
public sealed record RagSearchHit(string DocumentId, string Chunk, double Score);
