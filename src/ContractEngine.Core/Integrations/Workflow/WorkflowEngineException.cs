namespace ContractEngine.Core.Integrations.Workflow;

/// <summary>
/// Raised when a call to the Workflow Engine fails permanently — HTTP non-success after retries
/// or the circuit breaker is open. Callers (<c>ContractService</c>, etc.) catch and log; the
/// exception is NEVER re-thrown to the domain caller because workflow triggering is best-effort
/// and must not roll back the domain transaction that produced the event.
/// </summary>
public sealed class WorkflowEngineException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public WorkflowEngineException(string message, int? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public WorkflowEngineException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
