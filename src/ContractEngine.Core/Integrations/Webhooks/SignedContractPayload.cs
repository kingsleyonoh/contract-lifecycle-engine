namespace ContractEngine.Core.Integrations.Webhooks;

/// <summary>
/// Normalised signed-contract payload produced by <see cref="WebhookPayloadParser"/>.
/// DocuSign <c>envelope.completed</c> and PandaDoc <c>document_state_changed</c> payloads share
/// nothing structurally so the parser converts both into this common shape (PRD §5.6c) — the
/// webhook endpoint handler stays source-agnostic.
/// </summary>
public sealed record SignedContractPayload(
    string Source,
    string ExternalId,
    string Title,
    string CounterpartyName,
    string DownloadUrl,
    string FileName,
    DateTimeOffset? CompletedAt);
