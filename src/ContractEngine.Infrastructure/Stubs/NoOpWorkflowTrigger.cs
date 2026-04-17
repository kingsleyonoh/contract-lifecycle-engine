using ContractEngine.Core.Integrations.Workflow;
using ContractEngine.Core.Interfaces;

namespace ContractEngine.Infrastructure.Stubs;

/// <summary>
/// No-op <see cref="IWorkflowTrigger"/> registered when <c>WORKFLOW_ENGINE_ENABLED=false</c>.
///
/// <para>Returns <see cref="WorkflowTriggerResult.Triggered"/> = <c>false</c> — it does NOT throw.
/// Triggering a workflow is best-effort; throwing here would roll back whichever domain
/// transaction requested the workflow, which is a far worse failure mode than a missed trigger.
/// Call sites log the not-triggered outcome and move on.</para>
/// </summary>
public sealed class NoOpWorkflowTrigger : IWorkflowTrigger
{
    public Task<WorkflowTriggerResult> TriggerWorkflowAsync(
        string webhookPath,
        object payload,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkflowTriggerResult(Triggered: false));
}
