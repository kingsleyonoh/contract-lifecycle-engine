namespace ContractEngine.Core.Integrations.Rag;

/// <summary>
/// Upload acknowledgement returned by the RAG Platform's <c>POST /api/documents</c> endpoint.
/// The <see cref="Id"/> is the handle downstream callers persist on <c>contract_documents.rag_document_id</c>
/// so subsequent search / chat calls can filter by it.
/// </summary>
public sealed record RagDocument(string Id, string FileName, string Status);
