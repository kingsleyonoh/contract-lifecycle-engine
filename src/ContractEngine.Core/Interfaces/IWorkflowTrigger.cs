using ContractEngine.Core.Integrations.Workflow;

namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Abstraction over the Workflow Automation Engine (PRD §5.6d). The real implementation
/// (<c>WorkflowEngineClient</c>, Infrastructure layer) is a typed HTTP client with retries and a
/// circuit breaker; the no-op stub (<c>NoOpWorkflowTrigger</c>) is registered when
/// <c>WORKFLOW_ENGINE_ENABLED=false</c>.
///
/// <para>Return-shape policy: the no-op returns <see cref="WorkflowTriggerResult.Triggered"/> =
/// <c>false</c> and does NOT throw. Triggering is best-effort — e.g. notifying the engine that a
/// contract was terminated — and a missed trigger must not roll back the domain transaction.</para>
/// </summary>
public interface IWorkflowTrigger
{
    /// <summary>
    /// Triggers a workflow by POSTing a JSON payload to the Workflow Engine at
    /// <c>/webhooks/{webhookPath}</c>. The engine is responsible for matching the path to a
    /// registered workflow definition and starting a new instance.
    /// </summary>
    /// <param name="webhookPath">
    /// Relative path of the webhook endpoint — e.g. <c>contract-amendment-approval</c>. Must NOT
    /// include a leading slash.
    /// </param>
    /// <param name="payload">
    /// Workflow-specific payload (serialised as snake_case JSON). Shape is dictated by the
    /// workflow definition; the client performs no validation.
    /// </param>
    Task<WorkflowTriggerResult> TriggerWorkflowAsync(
        string webhookPath,
        object payload,
        CancellationToken cancellationToken = default);
}
