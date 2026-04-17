namespace ContractEngine.Core.Integrations.Rag;

/// <summary>
/// Raised when a call to the RAG Platform fails permanently — either the HTTP layer returned a
/// non-success status after retries were exhausted, or the circuit breaker is open. Callers in the
/// extraction pipeline catch this to mark an <c>ExtractionJob</c> as failed / queued-for-retry.
///
/// <para>Carries the upstream HTTP status code (null when the failure was transport-level) and the
/// response body when one was readable — diagnostics only, never propagated to end users.</para>
/// </summary>
public sealed class RagPlatformException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public RagPlatformException(string message, int? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public RagPlatformException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
