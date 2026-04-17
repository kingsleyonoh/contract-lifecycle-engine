namespace ContractEngine.Core.Integrations.Rag;

/// <summary>
/// Synchronous chat response from <c>POST /api/chat/sync</c>. <see cref="Answer"/> is the
/// LLM-generated answer (often a JSON blob when the caller specified a <c>response_format</c>),
/// and <see cref="Sources"/> lists the document chunks the LLM cited.
/// </summary>
public sealed record RagChatResult(string Answer, IReadOnlyList<RagChatSource> Sources);

/// <summary>
/// A single chunk cited by the RAG chat answer.
/// </summary>
public sealed record RagChatSource(string DocumentId, string Chunk);
