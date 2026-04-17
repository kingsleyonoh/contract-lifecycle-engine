namespace ContractEngine.Core.Enums;

/// <summary>
/// Provenance of an <see cref="Models.Obligation"/>. <see cref="Manual"/> — entered via the API.
/// <see cref="RagExtraction"/> — produced by the Multi-Agent RAG Platform pipeline (Phase 2).
/// <see cref="Webhook"/> — arrived via the Webhook Ingestion Engine (signed contract ingest).
/// Persisted as snake_case lowercase strings per PRD §4.6: <c>'manual'</c>,
/// <c>'rag_extraction'</c>, <c>'webhook'</c>.
/// </summary>
public enum ObligationSource
{
    Manual = 0,
    RagExtraction = 1,
    Webhook = 2,
}
