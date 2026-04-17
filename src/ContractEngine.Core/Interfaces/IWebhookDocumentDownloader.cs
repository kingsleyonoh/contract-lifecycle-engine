namespace ContractEngine.Core.Interfaces;

/// <summary>
/// Streams the signed contract PDF bytes pointed at by a webhook payload. The URL in the payload
/// itself is already signed / time-limited by the upstream signing service (DocuSign / PandaDoc),
/// so no per-request auth headers are required by default.
/// </summary>
public interface IWebhookDocumentDownloader
{
    /// <summary>
    /// Streams the document bytes at <paramref name="downloadUrl"/>. Throws
    /// <c>WebhookDownloadException</c> on non-success HTTP status and <see cref="ArgumentException"/>
    /// when the URL is null / empty / whitespace.
    /// </summary>
    Task<Stream> DownloadAsync(string downloadUrl, CancellationToken cancellationToken = default);
}
