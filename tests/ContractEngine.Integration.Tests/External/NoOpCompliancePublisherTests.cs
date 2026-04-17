using ContractEngine.Core.Integrations.Compliance;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Unit-level tests for <see cref="NoOpCompliancePublisher"/>. Contract: publishing a
/// compliance event when the integration is disabled MUST NOT throw. The compliance ledger is a
/// trailing audit stream — missing events must never roll back the domain transaction.
/// </summary>
public class NoOpCompliancePublisherTests
{
    private readonly IComplianceEventPublisher _publisher = new NoOpCompliancePublisher();

    [Fact]
    public async Task PublishAsync_returns_false_without_throwing()
    {
        var envelope = new ComplianceEventEnvelope(
            EventType: "contract.terminated",
            TenantId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Payload: new { contract_id = Guid.NewGuid() });

        var result = await _publisher.PublishAsync("contract.terminated", envelope);

        result.Should().BeFalse();
    }

    [Fact]
    public void ComplianceLedgerException_wraps_inner()
    {
        var inner = new InvalidOperationException("reason");
        var ex = new ComplianceLedgerException("boom", inner);

        ex.Message.Should().Be("boom");
        ex.InnerException.Should().BeSameAs(inner);
    }
}
