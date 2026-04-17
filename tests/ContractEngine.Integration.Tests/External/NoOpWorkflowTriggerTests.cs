using ContractEngine.Core.Integrations.Workflow;
using ContractEngine.Core.Interfaces;
using ContractEngine.Infrastructure.Stubs;
using FluentAssertions;
using Xunit;

namespace ContractEngine.Integration.Tests.External;

/// <summary>
/// Unit-level tests for <see cref="NoOpWorkflowTrigger"/>. Contract: triggering a workflow when
/// the integration is disabled MUST NOT throw — call sites treat workflow dispatch as
/// best-effort and a missed trigger must never roll back the domain transaction that produced
/// the event.
/// </summary>
public class NoOpWorkflowTriggerTests
{
    private readonly IWorkflowTrigger _trigger = new NoOpWorkflowTrigger();

    [Fact]
    public async Task TriggerWorkflowAsync_returns_not_triggered_without_throwing()
    {
        var result = await _trigger.TriggerWorkflowAsync(
            "contract-amendment-approval",
            new { contract_id = Guid.NewGuid() });

        result.Should().NotBeNull();
        result.Triggered.Should().BeFalse();
        result.InstanceId.Should().BeNull();
    }

    [Fact]
    public async Task TriggerWorkflowAsync_accepts_empty_payload_without_throwing()
    {
        var result = await _trigger.TriggerWorkflowAsync("any-webhook", new { });
        result.Triggered.Should().BeFalse();
    }

    [Fact]
    public void WorkflowEngineException_carries_status_code_and_body()
    {
        var ex = new WorkflowEngineException("boom", statusCode: 502, responseBody: "bad gateway");

        ex.StatusCode.Should().Be(502);
        ex.ResponseBody.Should().Be("bad gateway");
        ex.Message.Should().Be("boom");
    }
}
