namespace ContractEngine.Core.Integrations.Notifications;

/// <summary>
/// Raised when a call to the Notification Hub fails permanently — either the HTTP layer returned a
/// non-success status after retries were exhausted, or the circuit breaker is open. The real
/// client catches this at the call site and logs it; the exception is NEVER re-thrown to the
/// domain caller because notification dispatch is fire-and-forget (see
/// <see cref="Interfaces.INotificationPublisher"/> docs).
///
/// <para>Carries the upstream HTTP status code (null when the failure was transport-level) and the
/// response body when one was readable — diagnostics only, never propagated to end users.</para>
/// </summary>
public sealed class NotificationHubException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public NotificationHubException(string message, int? statusCode = null, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public NotificationHubException(string message, Exception innerException, int? statusCode = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
