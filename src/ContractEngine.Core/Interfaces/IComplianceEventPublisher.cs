using ContractEngine.Core.Integrations.Compliance;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the Financial Compliance Ledger NATS JetStream publisher (PRD §5.6c). The real
/// implementation (<c>ComplianceLedgerNatsPublisher</c>) wraps a long-lived NATS connection; the
/// no-op stub (<c>NoOpCompliancePublisher</c>) is registered when <c>COMPLIANCE_LEDGER_ENABLED=false</c>.
///
/// <para>Return shape is <see cref="bool"/>: <c>true</c> when the message was accepted by the NATS
/// server, <c>false</c> for the no-op stub. The no-op does NOT throw — compliance publishing is a
/// trailing audit stream and must never roll back the domain transaction that produced the
/// event.</para>
///
/// <para>Canonical subjects (see PRD §5.6c): <c>contract.obligation.breached</c>,
/// <c>contract.renewed</c>, <c>contract.terminated</c>.</para>
/// </summary>
public interface IComplianceEventPublisher
{
    Task<bool> PublishAsync(
        string subject,
        ComplianceEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
