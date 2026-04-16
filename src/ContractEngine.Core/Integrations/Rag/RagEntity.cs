namespace ContractEngine.Core.Integrations.Rag;

/// <summary>
/// A named entity (party, date, amount, etc.) extracted from a RAG-ingested document.
/// <see cref="Type"/> is the entity category (free-form string from the RAG Platform's NER pipeline),
/// <see cref="Value"/> is the surface form, <see cref="Confidence"/> is in [0, 1].
/// </summary>
public sealed record RagEntity(string Type, string Value, double Confidence);
