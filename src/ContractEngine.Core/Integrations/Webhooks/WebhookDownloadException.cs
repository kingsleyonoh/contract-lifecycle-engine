namespace ContractEngine.Core.Integrations.Webhooks;

/// <summary>
/// Raised by <see cref="IWebhookDocumentDownloader"/> implementations when the upstream signing
/// service returns a non-success HTTP status while streaming the signed contract PDF. Carries the
/// upstream <see cref="StatusCode"/> so the webhook endpoint can log context without leaking body
/// bytes that might contain a signed URL.
/// </summary>
public sealed class WebhookDownloadException : Exception
{
    public int StatusCode { get; }

    public WebhookDownloadException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public WebhookDownloadException(int statusCode, string message, Exception? inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
