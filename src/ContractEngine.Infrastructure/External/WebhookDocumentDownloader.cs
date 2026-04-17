using ContractEngine.Core.Integrations.Webhooks;
using ContractEngine.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ContractEngine.Infrastructure.External;

/// <summary>
/// Streams signed-contract bytes from the URL supplied in the webhook payload. The upstream
/// signing service (DocuSign / PandaDoc) encodes authentication in the URL itself (signed /
/// time-limited), so this client does NOT inject any per-request auth headers.
///
/// <para>Any non-success HTTP status bubbles up as <see cref="WebhookDownloadException"/> carrying
/// the upstream code — the webhook endpoint then rolls back the draft contract that was created
/// up to that point. Retries are handled by the typed <see cref="HttpClient"/>'s resilience
/// pipeline configured in Infrastructure DI, not here.</para>
/// </summary>
public sealed class WebhookDocumentDownloader : IWebhookDocumentDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<WebhookDocumentDownloader> _logger;

    public WebhookDocumentDownloader(HttpClient http, ILogger<WebhookDocumentDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Stream> DownloadAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentException("downloadUrl must not be empty", nameof(downloadUrl));
        }

        var resp = await _http.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            // Do NOT log the URL itself — it may contain signed credentials.
            _logger.LogWarning(
                "Signed-contract download returned non-success status {StatusCode}",
                (int)resp.StatusCode);

            throw new WebhookDownloadException(
                (int)resp.StatusCode,
                $"Signed-contract download returned HTTP {(int)resp.StatusCode}");
        }

        return await resp.Content.ReadAsStreamAsync(cancellationToken);
    }
}
