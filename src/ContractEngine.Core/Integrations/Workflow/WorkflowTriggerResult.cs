namespace ContractEngine.Core.Integrations.Workflow;

/// <summary>
/// Result envelope for <see cref="Interfaces.IWorkflowTrigger.TriggerWorkflowAsync"/>.
/// <see cref="Triggered"/> is <c>true</c> when the Workflow Engine accepted the webhook,
/// <c>false</c> when the no-op stub ran (integration disabled). <see cref="InstanceId"/> carries
/// the engine's echoed workflow instance id when provided; diagnostic only.
/// </summary>
public sealed record WorkflowTriggerResult(bool Triggered, string? InstanceId = null);
