namespace ContractEngine.Core.Integrations.Compliance;

/// <summary>
/// Canonical envelope for events published to the Financial Compliance Ledger (PRD §5.6c). The
/// ledger is append-only and consumers key on <see cref="EventType"/> + <see cref="TenantId"/> for
/// regulatory audit trails. Shape matches the ledger's JetStream subject schema:
/// <c>{ event_type, source, tenant_id, timestamp, payload }</c>.
/// </summary>
public sealed record ComplianceEventEnvelope(
    string EventType,
    Guid TenantId,
    DateTimeOffset Timestamp,
    object Payload,
    string Source = "contract-engine");
