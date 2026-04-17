using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Interfaces;

namespace ContractEngine.Infrastructure.Stubs;

/// <summary>
/// No-op <see cref="IComplianceEventPublisher"/> registered when
/// <c>COMPLIANCE_LEDGER_ENABLED=false</c>.
///
/// <para>Returns <c>false</c> — it does NOT throw. Compliance publishing is a trailing audit
/// stream; throwing here would roll back whichever domain transaction produced the event
/// (obligation breach, contract termination, renewal), which is the wrong trade-off.</para>
/// </summary>
public sealed class NoOpCompliancePublisher : IComplianceEventPublisher
{
    public Task<bool> PublishAsync(
        string subject,
        ComplianceEventEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
